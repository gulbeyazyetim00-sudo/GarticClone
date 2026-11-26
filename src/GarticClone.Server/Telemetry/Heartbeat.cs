using System.Buffers.Binary;
using System.Collections.Concurrent;
using GarticClone.Server.Transport;
using GarticClone.Server.Utils;

namespace GarticClone.Server.Telemetry;


public sealed class Heartbeat(
    TimeSpan interval,
    int maxMissed = 3,
    Action<string, double>? onRtt = null,
    Action<string>? onDead = null)
{
    private readonly ConcurrentDictionary<string, State> _tracked = new();
    private Timer? _timer;

    public TimeSpan Interval { get; } = interval;

    public void Start() => _timer ??= new Timer(_ => Tick(), null, Interval, Interval);

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public void Track(string clientId, WebSocketConnection connection)
    {
        var state = new State(connection);
        _tracked[clientId] = state;

        connection.PongReceived += payload =>
        {
            double? rtt = null;
            lock (state)
            {
                if (state.PendingNonce is uint nonce && payload.Length == 4 &&
                    BinaryPrimitives.ReadUInt32BigEndian(payload) == nonce)
                {
                    rtt = Clock.NowMs - state.SentAtMs;
                    state.PendingNonce = null;
                    state.Missed = 0;
                }
            }
            if (rtt is double r) onRtt?.Invoke(clientId, r);
        };
        connection.Closed += (_, _) => Untrack(clientId);

        SendPing(state); // first sample right away
    }

    public void Untrack(string clientId) => _tracked.TryRemove(clientId, out _);

    public void Tick()
    {
        foreach (var (clientId, state) in _tracked)
        {
            bool dead = false;
            lock (state)
            {
                if (state.PendingNonce is not null && ++state.Missed >= maxMissed) dead = true;
            }

            if (dead)
            {
                _tracked.TryRemove(clientId, out _);
                onDead?.Invoke(clientId);
                state.Connection.Terminate();
                continue;
            }
            SendPing(state);
        }
    }

    private static void SendPing(State state)
    {
        var payload = new byte[4];
        lock (state)
        {
            state.Nonce = unchecked(state.Nonce + 1);
            BinaryPrimitives.WriteUInt32BigEndian(payload, state.Nonce);
            state.PendingNonce = state.Nonce;
            state.SentAtMs = Clock.NowMs;
        }
        state.Connection.Ping(payload);
    }

    private sealed class State(WebSocketConnection connection)
    {
        public WebSocketConnection Connection { get; } = connection;
        public uint Nonce;
        public uint? PendingNonce;
        public double SentAtMs;
        public int Missed;
    }
}
