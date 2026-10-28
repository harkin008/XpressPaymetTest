using Microsoft.Extensions.Time.Testing;
using XpressTest.OrderService.RateLimiting;

namespace XpressTest.Tests.RateLimiting;

public class SlidingWindowRateLimitStoreTests
{
    private static readonly RateLimitRule ThreePerMinute = new() { PermitLimit = 3, Window = TimeSpan.FromMinutes(1) };

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly SlidingWindowRateLimitStore _store;

    public SlidingWindowRateLimitStoreTests() => _store = new SlidingWindowRateLimitStore(_clock);

    [Fact]
    public void Allows_up_to_the_limit_then_rejects()
    {
        var results = Enumerable.Range(0, 4).Select(_ => _store.TryAcquire("k", ThreePerMinute)).ToList();

        Assert.Equal([true, true, true, false], results.Select(r => r.Allowed));
        Assert.Equal([2, 1, 0, 0], results.Select(r => r.Remaining));
        Assert.All(results, r => Assert.Equal(3, r.Limit));
    }

    [Fact]
    public void Has_no_burst_at_window_boundaries()
    {
        // A fixed one-minute window would reset at 00:01:00 and allow 3 more at 00:01:01.
        _clock.Advance(TimeSpan.FromSeconds(59));
        for (var i = 0; i < 3; i++) Assert.True(_store.TryAcquire("k", ThreePerMinute).Allowed);

        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.False(_store.TryAcquire("k", ThreePerMinute).Allowed);
    }

    [Fact]
    public void Frees_one_slot_as_each_request_leaves_the_rolling_window()
    {
        _store.TryAcquire("k", ThreePerMinute);                 // t=0
        _clock.Advance(TimeSpan.FromSeconds(30));
        _store.TryAcquire("k", ThreePerMinute);                 // t=30
        _clock.Advance(TimeSpan.FromSeconds(20));
        _store.TryAcquire("k", ThreePerMinute);                 // t=50

        _clock.Advance(TimeSpan.FromSeconds(9));                // t=59
        var rejected = _store.TryAcquire("k", ThreePerMinute);
        Assert.False(rejected.Allowed);
        Assert.Equal(TimeSpan.FromSeconds(1), rejected.ResetAfter);

        _clock.Advance(TimeSpan.FromSeconds(1));                // t=60: the t=0 request has expired
        Assert.True(_store.TryAcquire("k", ThreePerMinute).Allowed);

        var again = _store.TryAcquire("k", ThreePerMinute);     // next slot frees when t=30 expires
        Assert.False(again.Allowed);
        Assert.Equal(TimeSpan.FromSeconds(30), again.ResetAfter);
    }

    [Fact]
    public void Rejected_attempts_do_not_extend_the_lockout()
    {
        for (var i = 0; i < 3; i++) _store.TryAcquire("k", ThreePerMinute);

        for (var s = 0; s < 59; s++)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            Assert.False(_store.TryAcquire("k", ThreePerMinute).Allowed);
        }

        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(_store.TryAcquire("k", ThreePerMinute).Allowed);
    }

    [Fact]
    public void Keys_have_independent_budgets()
    {
        for (var i = 0; i < 3; i++) _store.TryAcquire("a", ThreePerMinute);

        Assert.False(_store.TryAcquire("a", ThreePerMinute).Allowed);
        Assert.True(_store.TryAcquire("b", ThreePerMinute).Allowed);
    }

    [Fact]
    public void Reset_reports_time_until_a_slot_frees()
    {
        var first = _store.TryAcquire("k", ThreePerMinute);
        Assert.Equal(TimeSpan.FromMinutes(1), first.ResetAfter);

        _clock.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal(TimeSpan.FromSeconds(45), _store.TryAcquire("k", ThreePerMinute).ResetAfter);
    }

    [Fact]
    public void Concurrent_requests_never_exceed_the_limit()
    {
        var allowed = 0;
        Parallel.For(0, 2_000, _ =>
        {
            if (_store.TryAcquire("k", ThreePerMinute).Allowed) Interlocked.Increment(ref allowed);
        });

        Assert.Equal(3, allowed);
    }

    [Fact]
    public void Sweeping_idle_keys_does_not_reset_active_ones()
    {
        for (var i = 0; i < 3; i++) _store.TryAcquire("busy", ThreePerMinute);
        _store.TryAcquire("idle", ThreePerMinute);

        _clock.Advance(TimeSpan.FromSeconds(61));
        for (var i = 0; i < 3; i++) Assert.True(_store.TryAcquire("busy", ThreePerMinute).Allowed); // triggers a sweep
        Assert.False(_store.TryAcquire("busy", ThreePerMinute).Allowed);
        Assert.True(_store.TryAcquire("idle", ThreePerMinute).Allowed);
    }
}
