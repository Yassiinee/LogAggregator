using LogAggregator.Shared;

namespace LogAggregator.Worker.Sources;

/// <summary>
/// A stream of log entries for the worker to publish. Batches rather than single entries are
/// yielded because a tailed file naturally produces several lines per read.
/// </summary>
internal interface ILogSource
{
    /// <summary>Human-readable description used in startup logging.</summary>
    string Name { get; }

    /// <summary>
    /// Yields batches until cancelled. Implementations are expected to run indefinitely and to
    /// recover from transient problems themselves rather than completing the sequence.
    /// </summary>
    IAsyncEnumerable<IReadOnlyList<LogMessage>> ReadAsync(CancellationToken cancellationToken);
}
