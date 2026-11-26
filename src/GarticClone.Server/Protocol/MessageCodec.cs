using System.Buffers.Binary;
using System.Text.Json;

namespace GarticClone.Server.Protocol;


public static class ProtocolInfo
{
    public const byte Version = 1;
    public const int HeaderSize = 16;
}

[Flags]
public enum MessageFlags : byte
{
    None = 0,
    Compressed = 0x01,   
    Batched = 0x02,      
    AckRequired = 0x04,  
}

public sealed record AppMessage(
    byte Version,
    byte Opcode,
    MessageFlags Flags,
    uint Seq,
    uint Timestamp,
    ReadOnlyMemory<byte> Payload,
    int ByteLength);

public sealed class ProtocolDecodeException(string message) : Exception(message);

public static class MessageCodec
{
    public static byte[] Encode(byte opcode, ReadOnlySpan<byte> payload = default,
        MessageFlags flags = MessageFlags.None, uint seq = 0, uint timestamp = 0)
    {
        var bytes = new byte[ProtocolInfo.HeaderSize + payload.Length];
        var span = bytes.AsSpan();
        span[0] = ProtocolInfo.Version;
        span[1] = opcode;
        span[2] = (byte)flags;
        span[3] = 0;
        BinaryPrimitives.WriteUInt32BigEndian(span[4..], seq);
        BinaryPrimitives.WriteUInt32BigEndian(span[8..], timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(span[12..], (uint)payload.Length);
        payload.CopyTo(span[ProtocolInfo.HeaderSize..]);
        return bytes;
    }

    public static AppMessage Decode(byte[] bytes)
    {
        if (bytes.Length < ProtocolInfo.HeaderSize)
            throw new ProtocolDecodeException($"message too short: {bytes.Length} < {ProtocolInfo.HeaderSize}");

        var span = bytes.AsSpan();
        byte version = span[0];
        if (version != ProtocolInfo.Version)
            throw new ProtocolDecodeException($"unsupported protocol version {version}");
        if (span[3] != 0)
            throw new ProtocolDecodeException("reserved byte must be 0");

        uint payloadLength = BinaryPrimitives.ReadUInt32BigEndian(span[12..]);
        int actual = bytes.Length - ProtocolInfo.HeaderSize;
        if (payloadLength != actual)
            throw new ProtocolDecodeException($"payload length mismatch: header says {payloadLength}, got {actual}");

        return new AppMessage(
            version,
            Opcode: span[1],
            Flags: (MessageFlags)span[2],
            Seq: BinaryPrimitives.ReadUInt32BigEndian(span[4..]),
            Timestamp: BinaryPrimitives.ReadUInt32BigEndian(span[8..]),
            Payload: bytes.AsMemory(ProtocolInfo.HeaderSize),
            ByteLength: bytes.Length);
    }
}


public static class JsonPayload
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static byte[] Encode<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public static T? Decode<T>(ReadOnlySpan<byte> payload) => JsonSerializer.Deserialize<T>(payload, Options);
}
