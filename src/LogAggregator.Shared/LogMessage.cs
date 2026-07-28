namespace LogAggregator.Shared;

/// <summary>
/// A single log entry as it travels Worker -> LogHub -> Blazor clients.
/// </summary>
/// <param name="Timestamp">
/// When the entry was produced. Always carried as UTC on the wire so that a worker and a
/// dashboard in different time zones agree; render with <c>ToLocalTime()</c> in the UI.
/// </param>
/// <param name="LogLevel">One of <see cref="LogLevels"/>: Info, Warning or Error.</param>
/// <param name="Message">The log text, without the level/timestamp prefix.</param>
public sealed record LogMessage(DateTime Timestamp, string LogLevel, string Message);
