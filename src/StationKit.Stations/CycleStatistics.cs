namespace StationKit.Stations;

public sealed record CycleSnapshot(int Count, TimeSpan Last, TimeSpan Average, TimeSpan Min, TimeSpan Max);

/// <summary>工站節拍（CT）統計。</summary>
public sealed class CycleStatistics
{
    private readonly object _sync = new();
    private int _count;
    private TimeSpan _last;
    private TimeSpan _total;
    private TimeSpan _min = TimeSpan.MaxValue;
    private TimeSpan _max;

    public void Record(TimeSpan cycleTime)
    {
        lock (_sync)
        {
            _count++;
            _last = cycleTime;
            _total += cycleTime;
            _min = cycleTime < _min ? cycleTime : _min;
            _max = cycleTime > _max ? cycleTime : _max;
        }
    }

    public CycleSnapshot Snapshot()
    {
        lock (_sync)
        {
            return _count == 0
                ? new(0, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero)
                : new(_count, _last, _total / _count, _min, _max);
        }
    }
}
