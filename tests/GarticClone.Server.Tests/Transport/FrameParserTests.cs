using GarticClone.Server.Tests.Framework;
using GarticClone.Server.Transport;

namespace GarticClone.Server.Tests.Transport;

public class FrameParserTests
{
    private static readonly byte[] Mask = [0x37, 0xfa, 0x21, 0x3d];
    private static readonly byte[] RfcHello = [0x81, 0x85, 0x37, 0xfa, 0x21, 0x3d, 0x7f, 0x9f, 0x4d, 0x51, 0x58];

    [Test("RFC 6455 §5.7 masked \"Hello\" example")]
    public void RfcExample()
    {
        var f = new FrameParser().Push(RfcHello).Single();
        Assert.Equal(WsOpcode.Text, f.Opcode);
        Assert.True(f.Fin);
        Assert.Equal("Hello", System.Text.Encoding.UTF8.GetString(f.Payload));
        Assert.Equal(11, f.TotalBytes);
    }

    [Test("encoder produces the same bytes as the RFC example")]
    public void EncoderMatchesRfc() =>
        Assert.SequenceEqual(RfcHello, FrameEncoder.Encode(WsOpcode.Text, "Hello"u8, mask: Mask));

    [Test("round-trip across length-encoding boundaries (0,125,126,65535,65536,200000)")]
    public void LengthBoundaries()
    {
        foreach (int size in new[] { 0, 125, 126, 65535, 65536, 200_000 })
        {
            var payload = Enumerable.Repeat((byte)0xAB, size).ToArray();
            var f = new FrameParser().Push(FrameEncoder.Encode(WsOpcode.Binary, payload, mask: Mask)).Single();
            Assert.Equal(size, f.Payload.Length, $"size {size}");
            Assert.True(f.Payload.SequenceEqual(payload), $"content {size}");
        }
    }

    [Test("byte-by-byte delivery and several frames in one chunk")]
    public void Streaming()
    {
        var a = FrameEncoder.Encode(WsOpcode.Binary, "abc"u8, mask: Mask);
        var b = FrameEncoder.Encode(WsOpcode.Ping, "p"u8, mask: Mask);
        var all = a.Concat(b).ToArray();

        var parser = new FrameParser();
        var got = new List<Frame>();
        foreach (var bt in all) got.AddRange(parser.Push([bt]));
        Assert.Equal(2, got.Count);
        Assert.Equal("abc", System.Text.Encoding.ASCII.GetString(got[0].Payload));
        Assert.Equal(WsOpcode.Ping, got[1].Opcode);
        Assert.Equal(0, parser.BufferedBytes);

        Assert.Equal(2, new FrameParser().Push(all).Count);
    }

    private static void ExpectClose(byte[] bytes, ushort code, long maxPayload = 1 << 20)
    {
        var ex = Assert.Throws<WsProtocolException>(() => new FrameParser(maxPayload).Push(bytes));
        Assert.Equal(code, ex.CloseCode);
    }

    [Test("rejects unmasked client frame")]
    public void Unmasked() => ExpectClose(FrameEncoder.Encode(WsOpcode.Binary, "x"u8), CloseCode.ProtocolError);

    [Test("rejects RSV bits and unknown opcodes")]
    public void RsvAndOpcode()
    {
        var f = FrameEncoder.Encode(WsOpcode.Binary, "x"u8, mask: Mask);
        f[0] |= 0x40;
        ExpectClose(f, CloseCode.ProtocolError);
        ExpectClose(FrameEncoder.Encode(0x3, "x"u8, mask: Mask), CloseCode.ProtocolError);
    }

    [Test("rejects fragmented or oversized control frames")]
    public void ControlFrames()
    {
        ExpectClose(FrameEncoder.Encode(WsOpcode.Ping, new byte[1], fin: false, mask: Mask), CloseCode.ProtocolError);
        ExpectClose(FrameEncoder.Encode(WsOpcode.Ping, new byte[126], mask: Mask), CloseCode.ProtocolError);
    }

    [Test("rejects payload over limit before it arrives (only header sent)")]
    public void TooBig()
    {
        byte[] header = [0x82, 0xFF, 0, 0, 0, 0, 0x10, 0, 0, 0]; // 64-bit length = 256 MiB
        ExpectClose(header, CloseCode.MessageTooBig, maxPayload: 1024);
    }
}
