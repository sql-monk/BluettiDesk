using BluettiDesk.Core.Protocol;

namespace BluettiDesk.Tests;

public class ModbusTests
{
    [Fact]
    public void WriteFrameMatchesWhatTheRealStationEchoed()
    {
        // Captured from an AC180P: echo of "write 1 to register 2012".
        Assert.Equal(Convert.FromHexString("010607DC00018884"), Modbus.BuildWrite(2012, 1));
    }

    [Fact]
    public void ParsesARealReadReply()
    {
        // Captured from an AC180P: registers 110..115 hold the model name with swapped bytes.
        var reply = Convert.FromHexString("01030C4341383150300000000000002ACD");

        var values = Modbus.ParseRead(reply, 6);

        Assert.Equal(new ushort[] { 0x4341, 0x3831, 0x5030, 0, 0, 0 }, values);
    }

    [Fact]
    public void RejectsACorruptedReply()
    {
        var reply = Convert.FromHexString("01030C4341383150300000000000002ACE");

        Assert.False(Modbus.IsReadResponse(reply, 6));
        Assert.Throws<ProtocolException>(() => Modbus.ParseRead(reply, 6));
    }

    [Fact]
    public void RejectsAReplyOfTheWrongSize()
    {
        var reply = Convert.FromHexString("01030C4341383150300000000000002ACD");

        Assert.False(Modbus.IsReadResponse(reply, 5));
    }

    [Fact]
    public void ReportsAModbusException()
    {
        var frame = new byte[] { 1, 0x83, 2, 0, 0 };
        var crc = Modbus.Crc16(frame.AsSpan(0, 3));
        frame[3] = (byte)crc;
        frame[4] = (byte)(crc >> 8);

        var error = Assert.Throws<ModbusException>(() => Modbus.ParseRead(frame, 6));

        Assert.Equal(2, error.Code);
    }
}
