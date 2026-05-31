namespace BlackIce.Server.LoadBalancing.Authority;

/// <summary>
/// A fixed-duration sliding window of timestamped samples, for "how many events / how much total in
/// the last T seconds" rate checks (fire rate, hit rate, cumulative damage, event flood). Each sample
/// carries a value (1 for plain counting, or e.g. a damage amount for sums). Not thread-safe by design:
/// the relay drives interceptors from the single listener thread, so per-actor meters need no locking.
/// </summary>
public sealed class SlidingWindowCounter
{
    private readonly TimeSpan _window;
    private readonly Queue<(DateTime When, double Value)> _samples = new();
    private double _sum;

    public SlidingWindowCounter(TimeSpan window) => _window = window;

    /// <summary>Records a sample at <paramref name="now"/> (value defaults to 1) and evicts expired ones.</summary>
    public void Add(DateTime now, double value = 1)
    {
        _samples.Enqueue((now, value));
        _sum += value;
        Evict(now);
    }

    /// <summary>Number of samples within the window as of <paramref name="now"/>.</summary>
    public int Count(DateTime now) { Evict(now); return _samples.Count; }

    /// <summary>Sum of sample values within the window as of <paramref name="now"/>.</summary>
    public double Sum(DateTime now) { Evict(now); return _sum; }

    private void Evict(DateTime now)
    {
        var cutoff = now - _window;
        while (_samples.Count > 0 && _samples.Peek().When < cutoff)
            _sum -= _samples.Dequeue().Value;
        if (_samples.Count == 0) _sum = 0;   // guard against float drift once the window empties
    }
}
