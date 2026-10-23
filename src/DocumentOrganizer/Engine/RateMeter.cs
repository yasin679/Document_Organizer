namespace DocumentOrganizer.Engine;

/// <summary>
/// Measures progress speed over the last two minutes (a sliding window), so the estimate follows the current pace
/// rather than the average since the start (which is skewed by quick steps such as files already at the destination).
/// </summary>
public sealed class RateMeter
{
    private readonly Queue<(DateTime T, long N, long B)> _q = new();
    private readonly object _gate = new();
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(2);

    public RateMeter(long startCount = 0, long startBytes = 0) => Mark(startCount, startBytes);

    public DateTime Started { get; } = DateTime.UtcNow;

    /// <summary>Records the count (and bytes) done so far.</summary>
    public void Mark(long count, long bytes = 0)
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if (_q.Count > 0 && (now - _q.Last().T).TotalMilliseconds < 200) return;
            _q.Enqueue((now, count, bytes));
            while (_q.Count > 2 && now - _q.Peek().T > Window) _q.Dequeue();
        }
    }

    /// <summary>Items per second and bytes per second over the window; zero until there are two samples a second apart.</summary>
    public (double PerSecond, double BytesPerSecond) Rate()
    {
        lock (_gate)
        {
            if (_q.Count < 2) return (0, 0);
            var a = _q.Peek(); var b = _q.Last();
            var s = (b.T - a.T).TotalSeconds;
            return s < 1 ? (0, 0) : (Math.Max(0, b.N - a.N) / s, Math.Max(0, b.B - a.B) / s);
        }
    }

    /// <summary>
    /// Seconds left: by bytes when sizes are known (files of very different sizes), otherwise by count.
    /// Null when there is no reliable speed yet.
    /// </summary>
    public long? EtaSeconds(long remainingCount, long remainingBytes = 0)
    {
        if (remainingCount <= 0) return 0;
        var (perSec, bytesPerSec) = Rate();
        double? eta = remainingBytes > 0 && bytesPerSec > 0 ? remainingBytes / bytesPerSec : perSec > 0 ? remainingCount / perSec : null;
        return eta == null ? null : (long)Math.Ceiling(Math.Min(eta.Value, 30 * 24 * 3600));
    }
}

/// <summary>Live copy progress of a running job (kept in memory while it runs).</summary>
public sealed class CopyProgress
{
    public long Done, Total, BytesDone, BytesTotal, Copied, Skipped, Failed;
    public RateMeter Meter = new();
}
