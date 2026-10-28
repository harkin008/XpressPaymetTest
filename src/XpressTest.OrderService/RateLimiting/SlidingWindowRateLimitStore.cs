using System.Collections.Concurrent;

namespace XpressTest.OrderService.RateLimiting;

/// <param name="ResetAfter">Time until the oldest counted request leaves the window and frees a slot.</param>
public readonly record struct RateLimitDecision(bool Allowed, int Limit, int Remaining, TimeSpan ResetAfter);

public interface IRateLimitStore
{
    /// <summary>Counts one request for <paramref name="key"/> if the rule allows it.</summary>
    RateLimitDecision TryAcquire(string key, RateLimitRule rule);
}

/// <summary>
/// Sliding-log limiter: keeps the timestamp of every permitted request in the window, so the limit
/// holds for any rolling window, with no burst at window boundaries (unlike fixed windows).
/// Rejected requests are not recorded, so retrying while limited does not extend the lockout.
/// Memory per key is bounded by PermitLimit timestamps.
/// State is per process; with multiple instances, swap in a shared store (e.g. Redis sorted sets).
/// </summary>
public sealed class SlidingWindowRateLimitStore(TimeProvider clock) : IRateLimitStore
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, RequestLog> _logs = new();
    private long _nextSweepTicks;

    public RateLimitDecision TryAcquire(string key, RateLimitRule rule)
    {
        var now = clock.GetUtcNow().UtcTicks;
        MaybeSweep(now);

        while (true)
        {
            var log = _logs.GetOrAdd(key, _ => new RequestLog());
            lock (log)
            {
                // The sweeper removed this log after we fetched it; fetch the live one.
                if (log.Removed) continue;

                log.WindowTicks = rule.Window.Ticks;
                log.Evict(now);

                if (log.Hits.Count < rule.PermitLimit)
                {
                    log.Hits.Enqueue(now);
                    return new RateLimitDecision(true, rule.PermitLimit, rule.PermitLimit - log.Hits.Count, log.ResetAfter(now));
                }

                return new RateLimitDecision(false, rule.PermitLimit, 0, log.ResetAfter(now));
            }
        }
    }

    private void MaybeSweep(long now)
    {
        var next = Interlocked.Read(ref _nextSweepTicks);
        if (now < next || Interlocked.CompareExchange(ref _nextSweepTicks, now + SweepInterval.Ticks, next) != next)
            return;

        foreach (var (key, log) in _logs)
        {
            lock (log)
            {
                log.Evict(now);
                if (log.Hits.Count > 0) continue;
                log.Removed = true;
                _logs.TryRemove(new KeyValuePair<string, RequestLog>(key, log));
            }
        }
    }

    private sealed class RequestLog
    {
        public readonly Queue<long> Hits = new();
        public long WindowTicks;
        public bool Removed;

        public void Evict(long now)
        {
            while (Hits.Count > 0 && Hits.Peek() <= now - WindowTicks)
                Hits.Dequeue();
        }

        public TimeSpan ResetAfter(long now) =>
            Hits.Count == 0 ? TimeSpan.Zero : TimeSpan.FromTicks(Hits.Peek() + WindowTicks - now);
    }
}
