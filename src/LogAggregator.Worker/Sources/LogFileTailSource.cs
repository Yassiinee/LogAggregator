using System.Runtime.CompilerServices;
using System.Text;
using LogAggregator.Shared;
using Microsoft.Extensions.Options;

namespace LogAggregator.Worker.Sources;

/// <summary>
/// Tails a text file the way <c>tail -f</c> does: opens it without locking the writer out,
/// seeks to the end, then polls for appended bytes.
/// </summary>
/// <remarks>
/// Three details make the difference between this and a naive <c>ReadLine</c> loop:
/// <list type="bullet">
/// <item>Bytes are decoded incrementally and only complete lines are emitted, so a line that is
/// still being written is never published as a truncated fragment.</item>
/// <item>The file is opened with <see cref="FileShare.ReadWrite"/> | <see cref="FileShare.Delete"/>
/// so the application doing the logging is never blocked by this reader.</item>
/// <item>Truncation and rotation are detected and the file is reopened, instead of the tailer
/// going silent for the rest of the process lifetime.</item>
/// </list>
/// </remarks>
internal sealed class LogFileTailSource(
    IOptions<LogSourceOptions> options,
    ILogger<LogFileTailSource> logger) : ILogSource
{
    private const int ReadBufferSize = 8 * 1024;

    private readonly LogSourceOptions _options = options.Value;

    public string Name => $"file '{Path.GetFullPath(_options.FilePath)}'";

    public async IAsyncEnumerable<IReadOnlyList<LogMessage>> ReadAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string path = Path.GetFullPath(_options.FilePath);
        TimeSpan pollDelay = TimeSpan.FromMilliseconds(Math.Max(50, _options.FilePollMilliseconds));

        // Only the very first open honours ReadExistingContentOnStartup; after a rotation we
        // always read the replacement file from the beginning, since it is all new content.
        bool seekToEnd = !_options.ReadExistingContentOnStartup;
        bool waitingLogged = false;

        logger.LogInformation("Tailing {Path} (poll interval {Poll}).", path, pollDelay);

        while (!cancellationToken.IsCancellationRequested)
        {
            (FileStream Stream, DateTime OpenedAtUtc)? opened = TryOpen(path);
            if (opened is null)
            {
                if (!waitingLogged)
                {
                    logger.LogWarning("Log file {Path} is not available yet; waiting for it to appear.", path);
                    waitingLogged = true;
                }

                await Task.Delay(pollDelay, cancellationToken);
                continue;
            }

            waitingLogged = false;
            (FileStream? stream, DateTime openedAtUtc) = opened.Value;

            await using (stream)
            {
                if (seekToEnd)
                {
                    stream.Seek(0, SeekOrigin.End);
                }

                seekToEnd = false;

                Decoder decoder = Encoding.UTF8.GetDecoder();
                byte[] bytes = new byte[ReadBufferSize];
                char[] chars = new char[Encoding.UTF8.GetMaxCharCount(ReadBufferSize)];
                StringBuilder pending = new();
                bool reopen = false;

                while (!cancellationToken.IsCancellationRequested && !reopen)
                {
                    int read = await ReadChunkAsync(stream, bytes, cancellationToken);

                    switch (read)
                    {
                        case < 0: // read failed — drop the handle and start over
                            reopen = true;
                            continue;

                        case 0 when RotationDetected(stream, path, openedAtUtc):
                            logger.LogInformation("{Path} was truncated or rotated; reopening.", path);
                            reopen = true;
                            continue;

                        case 0:
                            await Task.Delay(pollDelay, cancellationToken);
                            continue;
                    }

                    int charCount = decoder.GetChars(bytes, 0, read, chars, 0);
                    pending.Append(chars, 0, charCount);

                    List<LogMessage> batch = ExtractCompleteLines(pending);
                    if (batch.Count > 0)
                    {
                        yield return batch;
                    }
                }
            }
        }
    }

    /// <summary>Opens the file for shared reading, or returns null if it is missing/locked.</summary>
    private (FileStream Stream, DateTime OpenedAtUtc)? TryOpen(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            // Captured before the open so a file replaced mid-open is caught on the next check.
            DateTime createdUtc = File.GetCreationTimeUtc(path);

            FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                ReadBufferSize,
                useAsync: true);

            return (stream, createdUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not open {Path}; will retry.", path);
            return null;
        }
    }

    /// <summary>Returns the byte count read, or -1 if the handle is no longer usable.</summary>
    private async Task<int> ReadChunkAsync(FileStream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        try
        {
            return await stream.ReadAsync(buffer, cancellationToken);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Read failed while tailing; reopening the file.");
            return -1;
        }
    }

    /// <summary>
    /// Detects the two ways a log file stops being the file we opened: it was emptied
    /// (length fell behind our position) or replaced by a new file at the same path.
    /// </summary>
    private static bool RotationDetected(FileStream stream, string path, DateTime openedAtUtc)
    {
        try
        {
            // File was truncated (no longer as long as our read position).
            if (stream.Length < stream.Position)
            {
                return true;
            }

            // FileShare.Delete means our handle survives a replace, still pointing at the old
            // file — so compare the creation stamp of whatever now lives at the path.
            return !File.Exists(path) || File.GetCreationTimeUtc(path) != openedAtUtc;
        }
        catch (IOException)
        {
            return true;
        }
    }

    /// <summary>
    /// Pulls whole lines out of the decode buffer, leaving any partially written trailing
    /// line in <paramref name="pending"/> for the next read.
    /// </summary>
    private static List<LogMessage> ExtractCompleteLines(StringBuilder pending)
    {
        List<LogMessage> batch = new();
        string text = pending.ToString();
        int start = 0;

        int newline;
        while ((newline = text.IndexOf('\n', start)) >= 0)
        {
            ReadOnlySpan<char> line = text.AsSpan(start, newline - start).Trim();
            start = newline + 1;

            if (!line.IsEmpty)
            {
                batch.Add(LogLineParser.Parse(line.ToString()));
            }
        }

        pending.Clear();
        if (start < text.Length)
        {
            pending.Append(text, start, text.Length - start);
        }

        return batch;
    }
}
