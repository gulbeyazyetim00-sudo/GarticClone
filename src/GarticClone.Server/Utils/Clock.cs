using System.Diagnostics;

namespace GarticClone.Server.Utils;

public static class Clock
{
    private static readonly long Origin = Stopwatch.GetTimestamp();

    /// <summary>Monotonic milliseconds since server start. Use for RTT/rates, never DateTime.Now.</summary>
    public static double NowMs => (Stopwatch.GetTimestamp() - Origin) * 1000.0 / Stopwatch.Frequency;

    /// <summary>u32 protocol timestamp: ms since server start, wraps after ~49.7 days.</summary>
    public static uint ProtocolTimestamp => unchecked((uint)(long)NowMs);
}
