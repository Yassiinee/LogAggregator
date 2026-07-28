namespace LogAggregator.Shared;

/// <summary>
/// The three levels the dashboard filters on. Kept as strings (not an enum) so the
/// contract stays tolerant of whatever a log file happens to contain.
/// </summary>
public static class LogLevels
{
    public const string Info = "Info";
    public const string Warning = "Warning";
    public const string Error = "Error";

    /// <summary>Display order used by the filter toolbar.</summary>
    public static readonly string[] All = [Error, Warning, Info];

    /// <summary>
    /// Collapses the many spellings found in real log files onto one of the three
    /// canonical levels. Anything unrecognised is treated as <see cref="Info"/>.
    /// </summary>
    public static string Normalize(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "ERROR" or "ERR" or "FATAL" or "CRITICAL" or "CRIT" => Error,
        "WARNING" or "WARN" or "WRN" => Warning,
        _ => Info,
    };
}
