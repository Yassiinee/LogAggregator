using System.Collections.Frozen;
using System.Globalization;
using System.Text.RegularExpressions;
using LogAggregator.Shared;

namespace LogAggregator.Worker;

/// <summary>
/// Turns a raw text line into a <see cref="LogMessage"/>. Recognises the common shapes
/// <c>2026-07-28 09:15:02.123 [ERROR] text</c>, <c>(WARN) text</c> and <c>INFO: text</c>.
/// A line it cannot classify is still published — as <see cref="LogLevels.Info"/> stamped
/// with the current time — because silently dropping log lines is worse than mislabelling one.
/// </summary>
internal static partial class LogLineParser
{
    // The level must be delimited — bracketed, parenthesised, or followed by ':' / '|'.
    // Without that requirement a message like "Information about X" would be read as a level.
    // .NET permits reusing a group name across alternation branches, so all three spellings
    // land in the same 'lvl' capture.
    [GeneratedRegex(
        """
        ^\s*
        (?:\[?(?<ts>\d{4}-\d{2}-\d{2}(?:[T ]\d{2}:\d{2}:\d{2}(?:[.,]\d{1,7})?(?:Z|[+-]\d{2}:?\d{2})?)?)\]?\s*)?
        (?:
            \[\s*(?<lvl>[A-Za-z]{3,11})\s*\]
          | \(\s*(?<lvl>[A-Za-z]{3,11})\s*\)
          | (?<lvl>[A-Za-z]{3,11})\s*[:|]
        )?
        \s*(?<msg>.*)$
        """,
        RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture | RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex LinePattern();

    /// <summary>Levels we accept as an actual level token rather than as the start of the message.</summary>
    private static readonly FrozenSet<string> KnownLevelTokens = FrozenSet.ToFrozenSet(new[]
    {
        "TRACE", "DEBUG", "DBG", "VERBOSE",
        "INFO", "INF", "INFORMATION",
        "WARN", "WRN", "WARNING",
        "ERROR", "ERR", "FATAL", "CRITICAL", "CRIT",
    }, StringComparer.OrdinalIgnoreCase);

    public static LogMessage Parse(string line)
    {
        Match match = LinePattern().Match(line);
        if (!match.Success)
        {
            return new LogMessage(DateTime.UtcNow, LogLevels.Info, line.Trim());
        }

        string? levelToken = match.Groups["lvl"].Success ? match.Groups["lvl"].Value : null;
        string message = match.Groups["msg"].Value.Trim();

        // A delimited word that is not a real level (e.g. "GET: /health") belongs to the message.
        if (levelToken is not null && !KnownLevelTokens.Contains(levelToken))
        {
            levelToken = null;
            message = StripTimestamp(line, match).Trim();
        }

        return new LogMessage(
            ParseTimestamp(match.Groups["ts"]),
            LogLevels.Normalize(levelToken),
            message);
    }

    /// <summary>Keeps everything after an optional leading timestamp, level token included.</summary>
    private static string StripTimestamp(string line, Match match)
    {
        Group ts = match.Groups["ts"];
        return ts.Success ? line[(ts.Index + ts.Length)..].TrimStart(' ', '\t', ']') : line;
    }

    private static DateTime ParseTimestamp(Group group)
    {
        if (!group.Success)
        {
            return DateTime.UtcNow;
        }

        // Some loggers use a comma as the fractional-second separator.
        string raw = group.Value.Replace(',', '.');

        return DateTime.TryParse(
            raw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeLocal,
            out DateTime timestamp)
            ? timestamp
            : DateTime.UtcNow;
    }
}
