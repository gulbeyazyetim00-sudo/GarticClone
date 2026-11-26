using System.Collections.Concurrent;
using GarticClone.Server.Protocol.Opcodes;
using GarticClone.Server.Transport;

namespace GarticClone.Server.Telemetry;

public sealed record DirectionStats(long Bytes, long Frames, long BytesPerSec);
public sealed record WireStats(DirectionStats In, DirectionStats Out);
public sealed record ByteCounter(long In, long Out);
public sealed record AppOpcodeStats(long In, long Out, long InCount, long OutCount);

public sealed record TelemetrySnapshot(
    string ClientId,
    long ConnectedForMs,
    WireStats Wire,
    long SendBufferBytes,
    IReadOnlyDictionary<string, ByteCounter> ByWsOpcode,
    IReadOnlyDictionary<string, AppOpcodeStats> ByAppOpcode,
    RttSnapshot Rtt);


public sealed class BandwidthMonitor(double windowMs = 5000, double bucketMs = 250)
{
    private readonly ConcurrentDictionary<string, ClientEntry> _clients = new();

    public void Attach(string clientId, WebSocketConnection connection)
    {
        var entry = new ClientEntry(connection, windowMs, bucketMs);
        _clients[clientId] = entry;
        connection.Traffic += entry.OnTraffic;
    }

    public void Detach(string clientId)
    {
        if (_clients.TryRemove(clientId, out var entry))
            entry.Connection.Traffic -= entry.OnTraffic;
    }

    public void RecordApp(string clientId, TrafficDirection direction, byte appOpcode, int bytes)
    {
        if (!_clients.TryGetValue(clientId, out var e)) return;
        lock (e.Lock)
        {
            var key = OpcodeRegistry.NameOf(appOpcode);
            var s = e.ByAppOpcode.GetValueOrDefault(key) ?? new AppOpcodeStats(0, 0, 0, 0);
            e.ByAppOpcode[key] = direction == TrafficDirection.In
                ? s with { In = s.In + bytes, InCount = s.InCount + 1 }
                : s with { Out = s.Out + bytes, OutCount = s.OutCount + 1 };
        }
    }

    public void RecordRtt(string clientId, double rttMs)
    {
        if (!_clients.TryGetValue(clientId, out var e)) return;
        lock (e.Lock) e.Rtt.AddSample(rttMs);
    }

    public TelemetrySnapshot? Snapshot(string clientId)
    {
        if (!_clients.TryGetValue(clientId, out var e)) return null;
        lock (e.Lock)
        {
            return new TelemetrySnapshot(
                clientId,
                (long)(DateTimeOffset.UtcNow - e.ConnectedAt).TotalMilliseconds,
                new WireStats(e.In.ToStats(), e.Out.ToStats()),
                e.Connection.BufferedAmount,
                new Dictionary<string, ByteCounter>(e.ByWsOpcode),
                new Dictionary<string, AppOpcodeStats>(e.ByAppOpcode),
                e.Rtt.Snapshot());
        }
    }

    public IReadOnlyList<TelemetrySnapshot> SnapshotAll() =>
        _clients.Keys.Select(Snapshot).OfType<TelemetrySnapshot>().ToList();

    private sealed class ClientEntry(WebSocketConnection connection, double windowMs, double bucketMs)
    {
        public readonly object Lock = new();
        public WebSocketConnection Connection { get; } = connection;
        public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;
        public Direction In { get; } = new(windowMs, bucketMs);
        public Direction Out { get; } = new(windowMs, bucketMs);
        public Dictionary<string, ByteCounter> ByWsOpcode { get; } = [];
        public Dictionary<string, AppOpcodeStats> ByAppOpcode { get; } = [];
        public RttEstimator Rtt { get; } = new();

        public void OnTraffic(TrafficEvent ev)
        {
            lock (Lock)
            {
                var dir = ev.Direction == TrafficDirection.In ? In : Out;
                dir.Bytes += ev.Bytes;
                dir.Frames++;
                dir.Rate.Add(ev.Bytes);

                var key = WsOpcode.Name(ev.WsOpcode);
                var c = ByWsOpcode.GetValueOrDefault(key) ?? new ByteCounter(0, 0);
                ByWsOpcode[key] = ev.Direction == TrafficDirection.In
                    ? c with { In = c.In + ev.Bytes }
                    : c with { Out = c.Out + ev.Bytes };
            }
        }
    }

    private sealed class Direction(double windowMs, double bucketMs)
    {
        public long Bytes;
        public long Frames;
        public SlidingWindowRate Rate { get; } = new(windowMs, bucketMs);
        public DirectionStats ToStats() => new(Bytes, Frames, (long)Math.Round(Rate.RatePerSecond()));
    }
}
