namespace LogAggregator.Shared;

/// <summary>
/// Single source of truth for the hub route and method names, shared by the server,
/// the worker (producer) and the Blazor UI (consumer) so the three cannot drift apart.
/// </summary>
public static class LogHubContract
{
    /// <summary>Route the hub is mapped on.</summary>
    public const string Path = "/hubs/logs";

    /// <summary>Server method the worker invokes to publish one entry.</summary>
    public const string BroadcastLog = nameof(BroadcastLog);

    /// <summary>Server method the worker invokes to publish several entries at once.</summary>
    public const string BroadcastLogBatch = nameof(BroadcastLogBatch);

    /// <summary>Client method the hub invokes to fan one entry out to dashboards.</summary>
    public const string ReceiveLog = nameof(ReceiveLog);

    /// <summary>
    /// Client method the hub invokes with several entries at once — used both for the
    /// on-connect history replay and for bursts coming out of a tailed file.
    /// </summary>
    public const string ReceiveLogBatch = nameof(ReceiveLogBatch);
}
