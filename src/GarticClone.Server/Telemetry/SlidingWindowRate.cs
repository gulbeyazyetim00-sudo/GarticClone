using GarticClone.Server.Utils;

namespace GarticClone.Server.Telemetry;


public sealed class SlidingWindowRate
{
    private readonly double _bucketMs;
    private readonly int _bucketCount;
    private readonly double[] _counts;
    private readonly long[] _slots;

    public SlidingWindowRate(double windowMs = 5000, double bucketMs = 250)
    {
        _bucketMs = bucketMs;
        _bucketCount = Math.Max(1, (int)Math.Ceiling(windowMs / bucketMs));
        _counts = new double[_bucketCount];
        _slots = Enumerable.Repeat(-1L, _bucketCount).ToArray();
    }

    public void Add(double amount, double? nowMs = null)
    {
        long slot = (long)Math.Floor((nowMs ?? Clock.NowMs) / _bucketMs);
        int i = (int)(slot % _bucketCount);
        if (_slots[i] != slot)
        {
            _slots[i] = slot;
            _counts[i] = 0;
        }
        _counts[i] += amount;
    }

    public double RatePerSecond(double? nowMs = null)
    {
        long current = (long)Math.Floor((nowMs ?? Clock.NowMs) / _bucketMs);
        double sum = 0;
        for (int i = 0; i < _bucketCount; i++)
            if (_slots[i] >= 0 && current - _slots[i] < _bucketCount) sum += _counts[i];
        return sum * 1000.0 / (_bucketCount * _bucketMs);
    }
}
