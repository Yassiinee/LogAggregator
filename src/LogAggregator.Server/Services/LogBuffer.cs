using LogAggregator.Shared;

namespace LogAggregator.Server.Services;

/// <summary>Bounded, thread-safe ring buffer for replaying recent log entries to newly connected clients.</summary>
public sealed class LogBuffer
{
    private readonly Queue<LogMessage> _entries;
    private readonly Lock _gate = new();

    public LogBuffer(int capacity = 500)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(capacity, 0);

        Capacity = capacity;
        _entries = new Queue<LogMessage>(capacity);
    }

    /// <summary>Maximum entries retained; zero disables replay.</summary>
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

    /// <summary>Adds a batch under a single lock, reducing contention.</summary>
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

    /// <summary>Point-in-time snapshot of entries (oldest first).</summary>
    public IReadOnlyList<LogMessage> Snapshot()
    {
        lock (_gate)
        {
            return [.. _entries];
        }
    }

    private void AddLocked(LogMessage message)
    {
        // Evict oldest when full
        while (_entries.Count >= Capacity)
        {
            _entries.Dequeue();
        }

        _entries.Enqueue(message);
    }
}
