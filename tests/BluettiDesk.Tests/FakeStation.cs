using System.Security.Cryptography;
using BluettiDesk.Core.Protocol;
using BluettiDesk.Core.Station;

namespace BluettiDesk.Tests;

/// <summary>
/// The station's side of the protocol, written independently of SecureChannel and signed with a test key,
/// so a handshake bug in the client cannot be hidden by the same bug here.
/// </summary>
internal sealed class FakeStation : IStationLink
{
    private const DSASignatureFormat RawSignature = DSASignatureFormat.IeeeP1363FixedFieldConcatenation;

    private readonly ECDsa _stationSigner;
    private readonly ECDsa _clientSigner;
    private readonly ECDiffieHellman _ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    private readonly byte[] _challenge = [0x5E, 0x7B, 0x50, 0xF4];
    private byte[] _tempIv = [];
    private byte[] _tempKey = [];
    private byte[]? _sessionKey;

    /// <param name="sameKeysAs">Another connection to the same station: a new session, the same fixed keys.</param>
    public FakeStation(FakeStation? sameKeysAs = null)
    {
        _stationSigner = sameKeysAs?._stationSigner ?? ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _clientSigner = sameKeysAs?._clientSigner ?? ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Keys = sameKeysAs?.Keys ?? new HandshakeKeys(
            RandomNumberGenerator.GetBytes(16),
            _clientSigner.ExportParameters(true).D!,
            _stationSigner.ExportSubjectPublicKeyInfo());
    }

    public event Action<byte[]>? NotificationReceived;

    public event Action? ConnectionLost;

    public HandshakeKeys Keys { get; }

    public Dictionary<int, ushort> Registers { get; } = [];

    /// <summary>When set, replies are cut into notifications of this size.</summary>
    public int? NotificationSize { get; set; }

    public bool SilentAfterHandshake { get; set; }

    public bool TamperWithStationSignature { get; set; }

    /// <summary>Answers every write with a Modbus exception, the way the station refuses a command.</summary>
    public bool RejectWrites { get; set; }

    /// <summary>Reads starting at these addresses get a Modbus exception.</summary>
    public HashSet<int> RejectReadsAt { get; } = [];

    /// <summary>Every (register, value) the client asked to write, refused or not.</summary>
    public List<(int Register, ushort Value)> Writes { get; } = [];

    public bool Disposed { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        NotificationReceived?.Invoke(WithChecksum([1, 4, .. _challenge]));
        return Task.CompletedTask;
    }

    public Task WriteAsync(byte[] data, CancellationToken cancellationToken)
    {
        if (data[0] == (byte)'*')
        {
            OnChallengeAnswer(data);
        }
        else if (_sessionKey is null)
        {
            OnClientKey(data);
        }
        else if (!SilentAfterHandshake)
        {
            OnRequest(data);
        }

        return Task.CompletedTask;
    }

    public void DropConnection() => ConnectionLost?.Invoke();

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }

    private static byte[] WithChecksum(byte[] body)
    {
        var sum = body.Sum(b => b);
        return [(byte)'*', (byte)'*', .. body, (byte)(sum >> 8), (byte)sum];
    }

    private static byte[] Pad(byte[] plain) => [.. plain, .. new byte[(16 - plain.Length % 16) % 16]];

    private void OnChallengeAnswer(byte[] data)
    {
        byte[] reversed = [_challenge[3], _challenge[2], _challenge[1], _challenge[0]];
        _tempIv = MD5.HashData(reversed);
        _tempKey = _tempIv.Zip(Keys.LocalAesKey, (a, b) => (byte)(a ^ b)).ToArray();
        Assert.Equal(WithChecksum([2, 4, .. _tempIv[8..12]]), data);

        NotificationReceived?.Invoke(WithChecksum([3, 1, 0]));

        var q = _ephemeral.ExportParameters(false).Q;
        byte[] point = [.. q.X!, .. q.Y!];
        byte[] signed = [.. point, .. _tempIv];
        var signature = _stationSigner.SignData(signed, HashAlgorithmName.SHA256, RawSignature);
        if (TamperWithStationSignature)
        {
            signature[0] ^= 0xFF;
        }

        var message = WithChecksum([4, 0x80, .. point, .. signature]);
        using var aes = Aes.Create();
        aes.Key = _tempKey;
        var cipher = aes.EncryptCbc(Pad(message), _tempIv, PaddingMode.None);
        NotificationReceived?.Invoke([(byte)(message.Length >> 8), (byte)message.Length, .. cipher]);
    }

    private void OnClientKey(byte[] frame)
    {
        using var aes = Aes.Create();
        aes.Key = _tempKey;
        var length = (frame[0] << 8) | frame[1];
        var plain = aes.DecryptCbc(frame.AsSpan(2), _tempIv, PaddingMode.None)[..length];
        Assert.Equal((byte)5, plain[2]);
        var point = plain[4..68];
        var signature = plain[68..132];
        byte[] signed = [.. point, .. _tempIv];
        Assert.True(_clientSigner.VerifyData(signed, signature, HashAlgorithmName.SHA256, RawSignature));

        using var client = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = point[..32], Y = point[32..] },
        });
        var sessionKey = _ephemeral.DeriveRawSecretAgreement(client.PublicKey);

        var accepted = WithChecksum([6, 1, 0]);
        var cipher = aes.EncryptCbc(Pad(accepted), _tempIv, PaddingMode.None);
        _sessionKey = sessionKey;
        NotificationReceived?.Invoke([(byte)(accepted.Length >> 8), (byte)accepted.Length, .. cipher]);
    }

    private void OnRequest(byte[] frame)
    {
        using var aes = Aes.Create();
        aes.Key = _sessionKey!;
        var length = (frame[0] << 8) | frame[1];
        var request = aes.DecryptCbc(frame.AsSpan(6), MD5.HashData(frame.AsSpan(2, 4)), PaddingMode.None)[..length];

        var address = (request[2] << 8) | request[3];
        var second = (request[4] << 8) | request[5];
        byte[] reply;
        if (request[1] == Modbus.WriteSingle)
        {
            Writes.Add((address, (ushort)second));
            if (RejectWrites)
            {
                byte[] exception = [1, Modbus.WriteSingle | 0x80, 3];
                var exceptionCrc = Modbus.Crc16(exception);
                reply = [.. exception, (byte)exceptionCrc, (byte)(exceptionCrc >> 8)];
            }
            else
            {
                Registers[address] = (ushort)second;
                reply = request;
            }
        }
        else if (RejectReadsAt.Contains(address))
        {
            byte[] exception = [1, Modbus.ReadHolding | 0x80, 2];
            var exceptionCrc = Modbus.Crc16(exception);
            reply = [.. exception, (byte)exceptionCrc, (byte)(exceptionCrc >> 8)];
        }
        else
        {
            var body = new List<byte> { 1, 3, (byte)(second * 2) };
            for (var i = 0; i < second; i++)
            {
                var value = Registers.GetValueOrDefault(address + i);
                body.Add((byte)(value >> 8));
                body.Add((byte)value);
            }

            var crc = Modbus.Crc16(body.ToArray());
            body.Add((byte)crc);
            body.Add((byte)(crc >> 8));
            reply = [.. body];
        }

        var seed = RandomNumberGenerator.GetBytes(4);
        var cipher = aes.EncryptCbc(Pad(reply), MD5.HashData(seed), PaddingMode.None);
        byte[] outgoing = [(byte)(reply.Length >> 8), (byte)reply.Length, .. seed, .. cipher];
        foreach (var chunk in outgoing.Chunk(NotificationSize ?? outgoing.Length))
        {
            NotificationReceived?.Invoke(chunk);
        }
    }
}
