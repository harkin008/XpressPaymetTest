namespace XpressTest.Shared.Models;

/// <summary>
/// Snowflake-style 64-bit ids, unique across every service without coordination:
/// 41 bits of milliseconds since <see cref="Epoch"/> (about 69 years), 10 bits of worker id
/// and a 12-bit sequence (4096 ids per millisecond per worker). Ids are positive and increase
/// over time. Each running service needs its own worker id (configuration <c>Ids:WorkerId</c>).
/// </summary>
public static class IdGenerator
{
    private const int WorkerBits = 10;
    private const int SequenceBits = 12;
    private const long SequenceMask = (1L << SequenceBits) - 1;
    public const int MaxWorkerId = (1 << WorkerBits) - 1;

    public static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly Lock Gate = new();
    private static long _workerId = -1;
    private static long _lastTimestamp = -1;
    private static long _sequence;

    public static void Configure(int workerId)
    {
        if (workerId is < 0 or > MaxWorkerId)
            throw new ArgumentOutOfRangeException(nameof(workerId), $"Worker id must be between 0 and {MaxWorkerId}.");
        lock (Gate) _workerId = workerId;
    }

    public static long NewId()
    {
        lock (Gate)
        {
            if (_workerId < 0)
                throw new InvalidOperationException("IdGenerator is not configured; call IdGenerator.Configure at startup.");

            // Never go back in time (clock adjustments), so ids stay unique and increasing.
            var timestamp = Math.Max((long)(DateTime.UtcNow - Epoch).TotalMilliseconds, _lastTimestamp);
            if (timestamp == _lastTimestamp)
            {
                _sequence = (_sequence + 1) & SequenceMask;
                // Sequence exhausted for this millisecond: borrow the next one instead of spinning.
                if (_sequence == 0) timestamp++;
            }
            else
            {
                _sequence = 0;
            }
            _lastTimestamp = timestamp;

            return (timestamp << (WorkerBits + SequenceBits)) | (_workerId << SequenceBits) | _sequence;
        }
    }
}
