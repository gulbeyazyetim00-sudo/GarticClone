using System.Buffers.Binary;
using System.Text;

namespace GarticClone.Server.Transport;

public static class FrameEncoder
{
    public static byte[] Encode(byte opcode, ReadOnlySpan<byte> payload, bool fin = true, byte[]? mask = null)
    {
        int len = payload.Length;
        int lengthBytes = len < 126 ? 0 : len < 0x10000 ? 2 : 8;
        int maskBytes = mask is null ? 0 : 4;
        int headerLen = 2 + lengthBytes + maskBytes;

        var frame = new byte[headerLen + len];
        frame[0] = (byte)((fin ? 0x80 : 0) | opcode);

        switch (lengthBytes)
        {
            case 0:
                frame[1] = (byte)len;
                break;
            case 2:
                frame[1] = 126;
                BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), (ushort)len);
                break;
            default:
                frame[1] = 127;
                BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(2), (ulong)len);
                break;
        }

        payload.CopyTo(frame.AsSpan(headerLen));

        if (mask is not null)
        {
            frame[1] |= 0x80;
            mask.CopyTo(frame, 2 + lengthBytes);
            FrameParser.ApplyMask(frame.AsSpan(headerLen), mask);
        }
        return frame;
    }

    public static byte[] ClosePayload(ushort? code, string reason = "")
    {
        if (code is null) return [];
        byte[] reasonBytes = Encoding.UTF8.GetBytes(reason);
        int reasonLen = Math.Min(reasonBytes.Length, WsConstants.MaxControlPayload - 2);

        var payload = new byte[2 + reasonLen];
        BinaryPrimitives.WriteUInt16BigEndian(payload, code.Value);
        Array.Copy(reasonBytes, 0, payload, 2, reasonLen);
        return payload;
    }
}
