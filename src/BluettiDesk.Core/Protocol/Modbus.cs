namespace BluettiDesk.Core.Protocol;

/// <summary>The station speaks plain Modbus RTU inside the encrypted channel: unit 1, CRC-16/MODBUS.</summary>
public static class Modbus
{
    public const byte ReadHolding = 0x03;
    public const byte WriteSingle = 0x06;
    private const byte Unit = 0x01;
    private const byte ExceptionFlag = 0x80;

    public static byte[] BuildRead(ushort address, ushort quantity) => Frame(ReadHolding, address, quantity);

    public static byte[] BuildWrite(ushort address, ushort value) => Frame(WriteSingle, address, value);

    /// <summary>True when <paramref name="response"/> is a CRC-valid answer to a read of that size, or an exception.</summary>
    public static bool IsReadResponse(ReadOnlySpan<byte> response, ushort quantity) =>
        IsException(response, ReadHolding)
        || (response.Length == 5 + 2 * quantity
            && response[0] == Unit
            && response[1] == ReadHolding
            && response[2] == 2 * quantity
            && HasValidCrc(response));

    /// <summary>A successful single-register write is echoed back byte for byte.</summary>
    public static bool IsWriteResponse(ReadOnlySpan<byte> response, ReadOnlySpan<byte> request) =>
        IsException(response, WriteSingle) || response.SequenceEqual(request);

    public static ushort[] ParseRead(ReadOnlySpan<byte> response, ushort quantity)
    {
        ThrowIfException(response, ReadHolding);
        if (!IsReadResponse(response, quantity))
        {
            throw new ProtocolException("Некоректна відповідь на читання регістрів.");
        }

        var values = new ushort[quantity];
        for (var i = 0; i < quantity; i++)
        {
            values[i] = (ushort)((response[3 + 2 * i] << 8) | response[4 + 2 * i]);
        }

        return values;
    }

    public static void ThrowIfException(ReadOnlySpan<byte> response, byte function)
    {
        if (IsException(response, function))
        {
            throw new ModbusException(response[2]);
        }
    }

    public static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
            }
        }

        return crc;
    }

    private static bool IsException(ReadOnlySpan<byte> response, byte function) =>
        response.Length == 5
        && response[0] == Unit
        && response[1] == (function | ExceptionFlag)
        && HasValidCrc(response);

    private static bool HasValidCrc(ReadOnlySpan<byte> frame)
    {
        var crc = Crc16(frame[..^2]);
        return frame[^2] == (byte)crc && frame[^1] == (byte)(crc >> 8);
    }

    private static byte[] Frame(byte function, ushort first, ushort second)
    {
        var frame = new byte[8];
        frame[0] = Unit;
        frame[1] = function;
        frame[2] = (byte)(first >> 8);
        frame[3] = (byte)first;
        frame[4] = (byte)(second >> 8);
        frame[5] = (byte)second;
        var crc = Crc16(frame.AsSpan(0, 6));
        frame[6] = (byte)crc;
        frame[7] = (byte)(crc >> 8);
        return frame;
    }
}

public class ProtocolException(string message) : Exception(message);

public sealed class ModbusException(byte code) : ProtocolException($"Станція відхилила запит (Modbus-код {code}).")
{
    public byte Code { get; } = code;
}
