using System.Collections.Concurrent;
using GarticClone.Server.Telemetry;
using GarticClone.Server.Transport;
using GarticClone.Server.Utils;

namespace GarticClone.Server.Protocol;

public sealed class SessionStats
{
    public int SeqGaps;
    public int DecodeErrors;
    public int UnknownOpcodes;
}


public sealed class ClientSession
{
    private readonly object _sendLock = new();
    private readonly BandwidthMonitor? _bandwidth;
    private uint _outSeq;
    private uint? _lastInSeq;

    public string Id { get; }
    public WebSocketConnection Connection { get; }
    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;
    public SessionStats Stats { get; } = new();

   
    public ConcurrentDictionary<string, object> Data { get; } = new();

    public ClientSession(WebSocketConnection connection, BandwidthMonitor? bandwidth = null)
    {
        Connection = connection;
        Id = $"c{connection.Id}";
        _bandwidth = bandwidth;
    }

   
    public int Send(byte opcode, ReadOnlySpan<byte> payload = default, MessageFlags flags = MessageFlags.None)
    {
        byte[] message;
        lock (_sendLock) 
        {
            message = MessageCodec.Encode(opcode, payload, flags, _outSeq, Clock.ProtocolTimestamp);
            if (!Connection.Send(message)) return -1;
            _outSeq = unchecked(_outSeq + 1);
        }
        _bandwidth?.RecordApp(Id, TrafficDirection.Out, opcode, message.Length);
        return message.Length;
    }

    public bool TrackInboundSeq(uint seq)
    {
        uint expected = _lastInSeq is null ? seq : unchecked(_lastInSeq.Value + 1);
        _lastInSeq = seq;
        if (seq == expected) return true;
        Interlocked.Increment(ref Stats.SeqGaps);
        return false;
    }

    public void Close(ushort code = CloseCode.Normal, string reason = "") => Connection.Close(code, reason);
}
