using System.Security.Cryptography;

namespace BluettiDesk.Core.Protocol;

/// <summary>
/// Constants of the station's key exchange. They are the same on every unit and were published by the
/// open-source projects bluetti_mqtt (nhurman) and bluetti-bt-lib (Patrick762); the test suite swaps
/// them so a fake station can sign its own messages.
/// </summary>
public sealed record HandshakeKeys(byte[] LocalAesKey, byte[] SigningKeyD, byte[] StationVerifyKeySpki)
{
    public static HandshakeKeys Default { get; } = new(
        Convert.FromHexString("459FC535808941F17091E0993EE3E93D"),
        Convert.FromHexString("4F19A16E3E87BDD9BD24D3E5495B88041511943CBC8B969ADE9641D0F56AF337"),
        Convert.FromHexString(
            "3059301306072A8648CE3D020106082A8648CE3D03010703420004"
            + "A73ABF5D2232C8C1C72E68304343C272495E3A8FD6F30EA96DE2F4B3CE60B251"
            + "EE21AC667CF8A71E18B46B664EAEFFE3C489F24F695B6411DB7E22CCC85A8594"));
}

/// <summary>
/// The encrypted link as a pure state machine: bytes in, bytes out, no I/O.
/// <para>
/// 1. The station sends a 4-byte challenge in clear. Its reversed MD5 is the IV of a temporary AES key.
/// 2. The station sends its signed P-256 key under that temporary key; we answer with our signed key.
/// 3. ECDH gives the session key. Every later frame is [length:2][iv seed:4][AES-256-CBC, zero padded].
/// </para>
/// </summary>
public sealed class SecureChannel(HandshakeKeys? keys = null)
{
    private const int BlockSize = 16;
    private const int KeyMaterialLength = 128;
    private static readonly byte[] Magic = "**"u8.ToArray();

    private readonly HandshakeKeys _keys = keys ?? HandshakeKeys.Default;
    private readonly List<byte> _buffer = [];
    private byte[]? _tempKey;
    private byte[]? _tempIv;
    private byte[]? _sessionKey;
    private ECDiffieHellman? _ephemeral;
    private ECDiffieHellmanPublicKey? _stationKey;

    public bool IsReady => _sessionKey is not null;

    /// <summary>Feeds one BLE notification. A frame may span several notifications.</summary>
    public ChannelResult Receive(ReadOnlySpan<byte> notification)
    {
        if (_buffer.Count == 0 && notification.StartsWith(Magic))
        {
            return new ChannelResult(HandleClearMessage(notification), null);
        }

        if (_tempKey is null)
        {
            return ChannelResult.Nothing;
        }

        _buffer.AddRange(notification);
        if (_buffer.Count < 2)
        {
            return ChannelResult.Nothing;
        }

        var length = (_buffer[0] << 8) | _buffer[1];
        var headerSize = IsReady ? 6 : 2;
        var total = headerSize + PaddedLength(length);
        if (_buffer.Count < total)
        {
            return ChannelResult.Nothing;
        }

        var frame = _buffer.GetRange(0, total).ToArray();
        _buffer.RemoveRange(0, total);
        var plain = Decrypt(frame, length);

        return plain.AsSpan().StartsWith(Magic)
            ? new ChannelResult(HandleEncryptedKeyMessage(plain), null)
            : new ChannelResult(null, plain);
    }

    /// <summary>Encrypts an application frame under the session key.</summary>
    public byte[] Seal(ReadOnlySpan<byte> plain)
    {
        if (_sessionKey is null)
        {
            throw new InvalidOperationException("Обмін ключами ще не завершено.");
        }

        var seed = RandomNumberGenerator.GetBytes(4);
        var cipher = Encrypt(plain, _sessionKey, IvFromSeed(seed));
        var frame = new byte[6 + cipher.Length];
        frame[0] = (byte)(plain.Length >> 8);
        frame[1] = (byte)plain.Length;
        seed.CopyTo(frame, 2);
        cipher.CopyTo(frame, 6);
        return frame;
    }

    /// <summary>Drops any partial frame, so a lost notification cannot poison the next reply.</summary>
    public void ResetFraming() => _buffer.Clear();

    // The station's protocol defines MD5 here; it only stretches a seed into an IV.
    private static byte[] IvFromSeed(ReadOnlySpan<byte> seed) => MD5.HashData(seed);

    private static int PaddedLength(int length) => (length + BlockSize - 1) / BlockSize * BlockSize;

    private static byte[] Encrypt(ReadOnlySpan<byte> plain, byte[] key, byte[] iv)
    {
        var padded = new byte[PaddedLength(plain.Length)];
        plain.CopyTo(padded);
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.EncryptCbc(padded, iv, PaddingMode.None);
    }

    private static byte[] Checksummed(ReadOnlySpan<byte> body)
    {
        var sum = 0;
        foreach (var b in body)
        {
            sum += b;
        }

        var message = new byte[Magic.Length + body.Length + 2];
        Magic.CopyTo(message, 0);
        body.CopyTo(message.AsSpan(Magic.Length));
        message[^2] = (byte)(sum >> 8);
        message[^1] = (byte)sum;
        return message;
    }

    /// <summary>Returns the data of a key-exchange message: magic, type, length, data, checksum.</summary>
    private static ReadOnlySpan<byte> ValidatedData(ReadOnlySpan<byte> message)
    {
        if (message.Length < 6)
        {
            throw new ProtocolException("Занадто коротке повідомлення обміну ключами.");
        }

        if (!message.SequenceEqual(Checksummed(message[2..^2])))
        {
            throw new ProtocolException("Неправильна контрольна сума під час обміну ключами.");
        }

        return message[4..^2];
    }

    private byte[] Decrypt(byte[] frame, int length)
    {
        using var aes = Aes.Create();
        byte[] plain;
        if (IsReady)
        {
            aes.Key = _sessionKey!;
            plain = aes.DecryptCbc(frame.AsSpan(6), IvFromSeed(frame.AsSpan(2, 4)), PaddingMode.None);
        }
        else
        {
            aes.Key = _tempKey!;
            plain = aes.DecryptCbc(frame.AsSpan(2), _tempIv!, PaddingMode.None);
        }

        return plain[..length];
    }

    private byte[]? HandleClearMessage(ReadOnlySpan<byte> message)
    {
        var data = ValidatedData(message);
        switch (message[2])
        {
            case 1:
                return AnswerChallenge(data);
            case 3:
                if (data.Length != 1 || data[0] != 0)
                {
                    throw new ProtocolException("Станція не прийняла відповідь на виклик.");
                }

                return null;
            default:
                return null;
        }
    }

    private byte[] AnswerChallenge(ReadOnlySpan<byte> challenge)
    {
        if (challenge.Length != 4)
        {
            throw new ProtocolException("Неочікувана довжина виклику.");
        }

        Span<byte> reversed = stackalloc byte[4];
        challenge.CopyTo(reversed);
        reversed.Reverse();

        _tempIv = IvFromSeed(reversed);
        _tempKey = new byte[BlockSize];
        for (var i = 0; i < BlockSize; i++)
        {
            _tempKey[i] = (byte)(_tempIv[i] ^ _keys.LocalAesKey[i]);
        }

        Span<byte> body = stackalloc byte[6];
        body[0] = 2;
        body[1] = 4;
        _tempIv.AsSpan(8, 4).CopyTo(body[2..]);
        return Checksummed(body);
    }

    private byte[]? HandleEncryptedKeyMessage(byte[] message)
    {
        var data = ValidatedData(message);
        switch (message[2])
        {
            case 4:
                return AnswerStationKey(data);
            case 6:
                FinishKeyExchange(data);
                return null;
            default:
                return null;
        }
    }

    private byte[] AnswerStationKey(ReadOnlySpan<byte> data)
    {
        if (data.Length != KeyMaterialLength)
        {
            throw new ProtocolException("Неочікувана довжина ключа станції.");
        }

        var stationPoint = data[..64];
        using (var verifier = ECDsa.Create())
        {
            verifier.ImportSubjectPublicKeyInfo(_keys.StationVerifyKeySpki, out _);
            var signed = Concat(stationPoint, _tempIv);
            if (!verifier.VerifyData(signed, data[64..], HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            {
                throw new ProtocolException("Підпис ключа станції не сходиться.");
            }
        }

        using (var station = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = stationPoint[..32].ToArray(), Y = stationPoint[32..].ToArray() },
        }))
        {
            _stationKey = station.PublicKey;
        }

        _ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var q = _ephemeral.ExportParameters(false).Q;
        var ourPoint = Concat(q.X, q.Y);

        byte[] signature;
        using (var signer = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = _keys.SigningKeyD,
        }))
        {
            signature = signer.SignData(Concat(ourPoint, _tempIv), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }

        var body = new byte[2 + KeyMaterialLength];
        body[0] = 5;
        body[1] = KeyMaterialLength;
        ourPoint.CopyTo(body, 2);
        signature.CopyTo(body, 2 + ourPoint.Length);
        var message = Checksummed(body);

        var cipher = Encrypt(message, _tempKey!, _tempIv!);
        var frame = new byte[2 + cipher.Length];
        frame[0] = (byte)(message.Length >> 8);
        frame[1] = (byte)message.Length;
        cipher.CopyTo(frame, 2);
        return frame;
    }

    private void FinishKeyExchange(ReadOnlySpan<byte> data)
    {
        if (data.Length != 1 || data[0] != 0 || _ephemeral is null || _stationKey is null)
        {
            throw new ProtocolException("Станція не прийняла наш ключ.");
        }

        _sessionKey = _ephemeral.DeriveRawSecretAgreement(_stationKey);
        _ephemeral.Dispose();
        _stationKey.Dispose();
        _ephemeral = null;
        _stationKey = null;
    }

    private static byte[] Concat(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        var result = new byte[first.Length + second.Length];
        first.CopyTo(result);
        second.CopyTo(result.AsSpan(first.Length));
        return result;
    }
}

/// <param name="ToSend">A handshake reply for the station, if any.</param>
/// <param name="Payload">A decrypted application frame, if one was completed.</param>
public readonly record struct ChannelResult(byte[]? ToSend, byte[]? Payload)
{
    public static ChannelResult Nothing => default;
}
