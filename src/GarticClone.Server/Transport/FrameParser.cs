using System.Buffers.Binary;

namespace GarticClone.Server.Transport;
public sealed record Frame(bool Fin, byte Opcode, byte[] Payload, int HeaderBytes, int TotalBytes);


public sealed class FrameParser(long maxPayload = 1 << 20, bool requireMask = true)
{
    private byte[] _buffer = new byte[8192];
    private int _length;

    public int BufferedBytes => _length;

    public List<Frame> Push(ReadOnlySpan<byte> chunk)
    {
        Append(chunk);

        var frames = new List<Frame>();
        int offset = 0;
        while (TryParse(offset, out var frame))
        {
            frames.Add(frame);
            offset += frame.TotalBytes;
        }

        if (offset > 0)
        {
            Buffer.BlockCopy(_buffer, offset, _buffer, 0, _length - offset);
            _length -= offset;
        }
        return frames;
    }

    private void Append(ReadOnlySpan<byte> chunk)
    {
        if (_length + chunk.Length > _buffer.Length)
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + chunk.Length));
        chunk.CopyTo(_buffer.AsSpan(_length));
        _length += chunk.Length;
    }

    private bool TryParse(int start, out Frame frame)
    {
        frame = null!;
        var data = _buffer.AsSpan(start, _length - start);
        if (data.Length < 2) return false;

        byte b0 = data[0], b1 = data[1];
        bool fin = (b0 & 0x80) != 0;
        int rsv = b0 & 0x70;
        byte opcode = (byte)(b0 & 0x0F);
        bool masked = (b1 & 0x80) != 0;
        long length = b1 & 0x7F;
        int headerLen = 2;

        if (rsv != 0)
            throw new WsProtocolException(CloseCode.ProtocolError, "RSV bits set without negotiated extension");
        if (!WsOpcode.IsKnown(opcode))
            throw new WsProtocolException(CloseCode.ProtocolError, $"unknown opcode 0x{opcode:X}");
        if (requireMask && !masked)
            throw new WsProtocolException(CloseCode.ProtocolError, "client frame not masked");
        if (!requireMask && masked)
            throw new WsProtocolException(CloseCode.ProtocolError, "server frame must not be masked");

        if (length == 126)
        {
            if (data.Length < 4) return false;
            length = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
            headerLen = 4;
        }
        else if (length == 127)
        {
            if (data.Length < 10) return false;
            ulong len64 = BinaryPrimitives.ReadUInt64BigEndian(data[2..]);
            if ((len64 >> 63) != 0)
                throw new WsProtocolException(CloseCode.ProtocolError, "64-bit length MSB must be 0");
            length = len64 > long.MaxValue ? long.MaxValue : (long)len64;
            headerLen = 10;
        }

        if (WsOpcode.IsControl(opcode))
        {
            if (!fin)
                throw new WsProtocolException(CloseCode.ProtocolError, "control frame must not be fragmented");
            if (length > WsConstants.MaxControlPayload)
                throw new WsProtocolException(CloseCode.ProtocolError, "control frame payload > 125");
        }
        if (length > maxPayload)
            throw new WsProtocolException(CloseCode.MessageTooBig, $"frame payload {length} > limit {maxPayload}");

        int maskLen = masked ? 4 : 0;
        long total = headerLen + maskLen + length;
        if (data.Length < total) return false;

        var payload = data.Slice(headerLen + maskLen, (int)length).ToArray();
        if (masked) ApplyMask(payload, data.Slice(headerLen, 4));

        frame = new Frame(fin, opcode, payload, headerLen + maskLen, (int)total);
        return true;
    }

    public static void ApplyMask(Span<byte> payload, ReadOnlySpan<byte> maskKey)
    {
        for (int i = 0; i < payload.Length; i++)
            payload[i] ^= maskKey[i & 3]; 
    }
}
