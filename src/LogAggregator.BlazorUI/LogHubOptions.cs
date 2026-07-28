using LogAggregator.Shared;

namespace LogAggregator.BlazorUI;

public sealed class LogHubOptions
{
    public const string SectionName = "LogHub";

    /// <summary>Root URL of LogAggregator.Server; the hub path comes from the shared contract.</summary>
    public string ServerBaseUrl { get; set; } = "http://localhost:5007";

    /// <summary>How many entries the terminal keeps in memory before dropping the oldest.</summary>
    public int MaxVisibleEntries { get; set; } = 2_000;

    /// <summary>
    /// Minimum gap between re-renders. Repainting on every single message makes the UI
    /// unusable under load, so incoming entries are coalesced into one render per interval.
    /// </summary>
    public int RenderIntervalMilliseconds { get; set; } = 100;

    public Uri HubUri => new(new Uri(ServerBaseUrl, UriKind.Absolute), LogHubContract.Path);
}
