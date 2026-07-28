using LogAggregator.Shared;

namespace LogAggregator.Server.Hubs;

/// <summary>
/// Strongly-typed client surface for <see cref="LogHub"/>. Using <c>Hub&lt;T&gt;</c> instead of
/// magic strings means a renamed/mistyped client method is a compile error on the server side.
/// The names must still match <see cref="LogHubContract"/>, which the clients subscribe with.
/// </summary>
public interface ILogClient
{
    /// <summary>Pushes a single new log entry to the dashboard.</summary>
    Task ReceiveLog(LogMessage message);

    /// <summary>
    /// Pushes several entries in one round trip: the history replay a freshly opened
    /// dashboard receives, and bursts of lines read from a tailed file.
    /// </summary>
    Task ReceiveLogBatch(IReadOnlyList<LogMessage> messages);
}
