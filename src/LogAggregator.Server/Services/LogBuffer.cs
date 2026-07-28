using LogAggregator.Shared;

namespace LogAggregator.Server.Services;

/// <summary>
/// Bounded, thread-safe ring buffer of the most recent log entries.
/// A hub is transient (one instance per invocation), so history has to live in a singleton.
/// Its only job is to keep a newly connected dashboard from starting empty.
/// </summary>
public sealed class LogBuffer(int capacity = 500)
{
    private readonly Queue<LogMessage> _entries = new(capacity);
    private readonly Lock _gate = new();

    public int Capacity { get; } = capacity;

    public void Add(LogMessage message)
    {
        lock (_gate)
        {
            if (_entries.Count == Capacity)
            {
                _entries.Dequeue();
            }

            _entries.Enqueue(message);
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
}
