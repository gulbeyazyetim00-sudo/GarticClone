using GarticClone.Server.Protocol;
using GarticClone.Server.Protocol.Opcodes;
using GarticClone.Server.Tests.Framework;

namespace GarticClone.Server.Tests.Protocol;

public class MessageCodecTests
{
    [Test("encode/decode round-trip")]
    public void RoundTrip()
    {
        byte[] payload = [1, 2, 3, 4, 5];
        var bytes = MessageCodec.Encode(SystemOp.PingApp, payload,
            MessageFlags.Compressed | MessageFlags.Batched, seq: 0xFFFFFFFE, timestamp: 123456);
        Assert.Equal(ProtocolInfo.HeaderSize + 5, bytes.Length);

        var m = MessageCodec.Decode(bytes);
        Assert.Equal(SystemOp.PingApp, m.Opcode);
        Assert.Equal(MessageFlags.Compressed | MessageFlags.Batched, m.Flags);
        Assert.Equal(0xFFFFFFFEu, m.Seq);
        Assert.Equal(123456u, m.Timestamp);
        Assert.SequenceEqual(payload, m.Payload.ToArray());
    }

    [Test("header layout is big-endian as documented")]
    public void Layout()
    {
        var b = MessageCodec.Encode(0x07, [9], seq: 0x01020304, timestamp: 0x0A0B0C0D);
        Assert.SequenceEqual(new byte[] { 1, 7, 0, 0, 1, 2, 3, 4, 0x0A, 0x0B, 0x0C, 0x0D, 0, 0, 0, 1, 9 }, b);
    }

    [Test("rejects short, wrong version, reserved byte, length mismatch")]
    public void Rejects()
    {
        Assert.Throws<ProtocolDecodeException>(() => MessageCodec.Decode(new byte[3]));

        var ok = MessageCodec.Encode(1, new byte[2]);
        var badVersion = (byte[])ok.Clone();
        badVersion[0] = 99;
        Assert.Throws<ProtocolDecodeException>(() => MessageCodec.Decode(badVersion));

        var badReserved = (byte[])ok.Clone();
        badReserved[3] = 1;
        Assert.Throws<ProtocolDecodeException>(() => MessageCodec.Decode(badReserved));

        Assert.Throws<ProtocolDecodeException>(() => MessageCodec.Decode(ok[..^1]));
    }

    [Test("opcode names and registry validation")]
    public void OpcodeNames()
    {
        OpcodeRegistry.Validate();
        Assert.Equal("system.Hello", OpcodeRegistry.NameOf(SystemOp.Hello));
        Assert.Equal("unknown(0xee)", OpcodeRegistry.NameOf(0xEE));
    }
}
