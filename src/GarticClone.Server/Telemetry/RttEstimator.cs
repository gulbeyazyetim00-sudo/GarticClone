namespace GarticClone.Server.Telemetry;

public sealed record RttSnapshot(double? LastMs, double? SrttMs, double? JitterMs, double? MinMs, double? MaxMs, int Samples);

public sealed class RttEstimator(double alpha = 1.0 / 8, double beta = 1.0 / 4)
{
    public double? Srtt { get; private set; }
    public double? RttVar { get; private set; }
    public double? Last { get; private set; }
    public double Min { get; private set; } = double.PositiveInfinity;
    public double Max { get; private set; }
    public int Samples { get; private set; }

    public void AddSample(double rttMs)
    {
        Last = rttMs;
        Samples++;
        Min = Math.Min(Min, rttMs);
        Max = Math.Max(Max, rttMs);

        if (Srtt is null)
        {
            Srtt = rttMs;
            RttVar = rttMs / 2;
        }
        else
        {
            RttVar = (1 - beta) * RttVar!.Value + beta * Math.Abs(Srtt.Value - rttMs);
            Srtt = (1 - alpha) * Srtt.Value + alpha * rttMs;
        }
    }

    public RttSnapshot Snapshot()
    {
        static double? R(double? v) => v is null || !double.IsFinite(v.Value) ? null : Math.Round(v.Value, 2);
        return new RttSnapshot(R(Last), R(Srtt), R(RttVar), Samples > 0 ? R(Min) : null, Samples > 0 ? R(Max) : null, Samples);
    }
}
