using LogAggregator.Shared;

namespace LogAggregator.Worker;

public enum LogSourceMode
{
    /// <summary>Tail the file if it exists at startup, otherwise fall back to the simulator.</summary>
    Auto = 0,

    /// <summary>Always tail <see cref="LogSourceOptions.FilePath"/>, waiting for it to appear.</summary>
    File = 1,

    /// <summary>Always generate synthetic traffic on a timer.</summary>
    Simulate = 2,
}

public sealed class LogSourceOptions
{
    public const string SectionName = "LogSource";

    /// <summary>Root URL of LogAggregator.Server; the hub path comes from the shared contract.</summary>
    public string ServerBaseUrl { get; set; } = "http://localhost:5007";

    public LogSourceMode Mode { get; set; } = LogSourceMode.Auto;

    /// <summary>File to tail. Relative paths resolve against the worker's working directory.</summary>
    public string FilePath { get; set; } = "logs/app.log.txt";

    /// <summary>
    /// When true, everything already in the file is published on startup. When false (default)
    /// the tailer seeks to the end and only reports lines appended from now on.
    /// </summary>
    public bool ReadExistingContentOnStartup { get; set; }

    /// <summary>How often to check a tailed file for appended bytes.</summary>
    public int FilePollMilliseconds { get; set; } = 400;

    /// <summary>Interval between synthetic entries in <see cref="LogSourceMode.Simulate"/>.</summary>
    public int SimulationIntervalMilliseconds { get; set; } = 800;

    /// <summary>Upper bound on entries per hub invocation, so a log flood cannot build one huge frame.</summary>
    public int MaxBatchSize { get; set; } = 50;

    /// <summary>Absolute hub endpoint the SignalR client connects to.</summary>
    public Uri HubUri => new(new Uri(ServerBaseUrl, UriKind.Absolute), LogHubContract.Path);

    /// <summary>Fail fast on nonsense configuration instead of misbehaving at runtime.</summary>
    public void Validate()
    {
        if (!Uri.TryCreate(ServerBaseUrl, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(ServerBaseUrl)} must be an absolute URL (got '{ServerBaseUrl}').");
        }

        if (Mode is LogSourceMode.File && string.IsNullOrWhiteSpace(FilePath))
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(FilePath)} is required when {nameof(Mode)} is {nameof(LogSourceMode.File)}.");
        }

        if (MaxBatchSize < 1)
        {
            throw new InvalidOperationException($"{SectionName}:{nameof(MaxBatchSize)} must be at least 1.");
        }
    }
}
