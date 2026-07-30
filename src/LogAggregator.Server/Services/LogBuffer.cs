using LogAggregator.Shared;

namespace LogAggregator.Server.Services;

/// <summary>
/// Bounded, thread-safe ring buffer of the most recent log entries.
/// A hub is transient (one instance per invocation), so history has to live in a singleton.
/// Its only job is to keep a newly connected dashboard from starting empty.
/// </summary>
public sealed class LogBuffer
{
    private readonly Queue<LogMessage> _entries;
    private readonly Lock _gate = new();

    public LogBuffer(int capacity = 500)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);

        Capacity = capacity;
        _entries = new Queue<LogMessage>(capacity);
    }

    /// <summary>Entries retained for replay. Zero is valid and disables replay entirely.</summary>
    public int Capacity { get; }

    public void Add(LogMessage message)
    {
        if (Capacity == 0)
        {
            return;
        }

        lock (_gate)
        {
            AddLocked(message);
        }
    }

    /// <summary>
    /// Adds a whole batch under a single lock acquisition. Doing this per entry made a
    /// 50-line batch contend for the gate 50 times against every other producer and every
    /// dashboard connecting at that moment.
    /// </summary>
    public void AddRange(ReadOnlySpan<LogMessage> messages)
    {
        if (Capacity == 0 || messages.IsEmpty)
        {
            return;
        }

        lock (_gate)
        {
            foreach (LogMessage message in messages)
            {
                AddLocked(message);
            }
        }
    }

    /// <summary>Point-in-time copy, oldest first.</summary>
    public IReadOnlyList<LogMessage> Snapshot()
    {
        lock (_gate)
        {
            return [.. _entries];
        }
    }

    /// <summary>Caller must hold <see cref="_gate"/>.</summary>
    private void AddLocked(LogMessage message)
    {
        while (_entries.Count >= Capacity)
        {
            _entries.Dequeue();
        }

        _entries.Enqueue(message);
    }
}
