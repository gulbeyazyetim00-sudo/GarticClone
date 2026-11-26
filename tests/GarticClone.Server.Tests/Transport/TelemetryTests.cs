using GarticClone.Server.Telemetry;
using GarticClone.Server.Tests.Framework;
using GarticClone.Server.Tests.Helpers;
using GarticClone.Server.Transport;

namespace GarticClone.Server.Tests.Telemetry;

public class TelemetryTests
{
    [Test("sliding window rate and expiry")]
    public void SlidingWindow()
    {
        var w = new SlidingWindowRate(windowMs: 1000, bucketMs: 100);
        w.Add(500, 10_000);
        w.Add(500, 10_550);
        Assert.Equal(1000.0, w.RatePerSecond(10_900)); // 1000 bytes over a 1 s window
        Assert.Equal(0.0, w.RatePerSecond(12_000));    // expired
    }

    [Test("RTT estimator follows RFC 6298 smoothing")]
    public void Rtt()
    {
        var r = new RttEstimator();
        r.AddSample(100);
        Assert.Equal(100.0, r.Srtt);
        Assert.Equal(50.0, r.RttVar);
        r.AddSample(200);
        Assert.Equal(0.75 * 50 + 0.25 * 100, r.RttVar);
        Assert.Equal(0.875 * 100 + 0.125 * 200, r.Srtt);
        Assert.Equal(200.0, r.Snapshot().MaxMs);
    }

    [Test("heartbeat measures RTT from the peer's automatic PONG")]
    public async Task HeartbeatRtt()
    {
        await using var pair = await LoopbackConnection.CreateAsync();
        var rtt = new TaskCompletionSource<double>();
        var hb = new Heartbeat(TimeSpan.FromHours(1), onRtt: (_, r) => rtt.TrySetResult(r));

        hb.Track("c1", pair.Server);              // sends first PING immediately
        _ = pair.ReceiveAsync();                  // ClientWebSocket answers PINGs while receiving
        double value = await rtt.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(value >= 0);
    }

    [Test("heartbeat terminates after maxMissed unanswered pings")]
    public async Task HeartbeatDead()
    {
        await using var pair = await LoopbackConnection.CreateAsync(); // client never reads -> never pongs
        var dead = new List<string>();
        var closed = new TaskCompletionSource<ushort>();
        pair.Server.Closed += (code, _) => closed.TrySetResult(code);

        var hb = new Heartbeat(TimeSpan.FromHours(1), maxMissed: 3, onDead: dead.Add);
        hb.Track("c1", pair.Server);
        hb.Tick();
        hb.Tick();
        Assert.Equal(0, dead.Count);
        hb.Tick();
        Assert.SequenceEqual(new[] { "c1" }, dead);
        Assert.Equal(CloseCode.Abnormal, await closed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
