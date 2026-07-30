using LogAggregator.Shared;
using LogAggregator.Worker.Sources;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;

namespace LogAggregator.Worker;

/// <summary>
/// The producer side of the dashboard: reads log entries from the configured source and
/// invokes <c>LogHub.BroadcastLog</c> / <c>BroadcastLogBatch</c> for each batch.
/// </summary>
internal sealed class Worker(
    HubConnection connection,
    LogFileTailSource fileSource,
    SimulatedLogSource simulatedSource,
    IOptions<LogSourceOptions> options,
    ILogger<Worker> logger) : BackgroundService
{
    /// <summary>
    /// Upper bound on one hub invocation's payload. The server accepts 1 MB, so chunking at
    /// half that leaves room for the estimate below to be wrong without the hub rejecting
    /// the frame.
    /// </summary>
    private const int MaxPayloadBytes = 512 * 1024;

    /// <summary>
    /// Longest single message we will publish. A tailed file can produce an arbitrarily long
    /// line (a minified payload, a corrupt file with no newlines); truncating keeps it under
    /// the frame limit instead of making it undeliverable.
    /// </summary>
    private const int MaxMessageChars = 32 * 1024;

    /// <summary>Invocation failures tolerated before a chunk is dropped rather than retried forever.</summary>
    private const int MaxPublishAttempts = 5;

    private readonly LogSourceOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ILogSource source = SelectSource();
        logger.LogInformation("Publishing to {HubUri} from {Source}.", _options.HubUri, source.Name);

        WireConnectionEvents();

        // WithAutomaticReconnect only covers drops after a first successful connect, so the
        // initial attempt is retried here — the worker normally starts before the server is up.
        await EnsureConnectedAsync(stoppingToken);

        await foreach (IReadOnlyList<LogMessage> batch in source.ReadAsync(stoppingToken))
        {
            await PublishAsync(batch, stoppingToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        try
        {
            await connection.StopAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Ignoring failure while closing the hub connection during shutdown.");
        }
    }

    /// <summary>
    /// Auto mode prefers a real file and falls back to the simulator, so the solution runs
    /// out of the box while still tailing a file the moment one exists.
    /// </summary>
    private ILogSource SelectSource()
    {
        switch (_options.Mode)
        {
            case LogSourceMode.File:
                return fileSource;

            case LogSourceMode.Simulate:
                return simulatedSource;

            default:
                string path = Path.GetFullPath(_options.FilePath);
                if (File.Exists(path))
                {
                    return fileSource;
                }

                logger.LogInformation(
                    "Auto mode: {Path} does not exist, using the simulator. Create the file (or set " +
                    "LogSource:Mode to \"File\") to tail it instead.", path);
                return simulatedSource;
        }
    }

    /// <summary>
    /// Splits a batch into frames the hub will accept and publishes them in order. Chunking
    /// on entry count alone is not enough: fifty long lines build a frame larger than the
    /// hub's receive limit, and that frame can never succeed no matter how often it is sent.
    /// </summary>
    private async Task PublishAsync(IReadOnlyList<LogMessage> batch, CancellationToken cancellationToken)
    {
        int offset = 0;

        while (offset < batch.Count && !cancellationToken.IsCancellationRequested)
        {
            int count = 0;
            int bytes = 0;

            while (offset + count < batch.Count
                   && count < _options.MaxBatchSize
                   && bytes < MaxPayloadBytes)
            {
                bytes += EstimateBytes(batch[offset + count]);
                count++;
            }

            LogMessage[] chunk = new LogMessage[count];
            for (int i = 0; i < count; i++)
            {
                chunk[i] = Clamp(batch[offset + i]);
            }

            offset += count;

            await PublishChunkAsync(chunk, cancellationToken);
        }
    }

    private async Task PublishChunkAsync(LogMessage[] chunk, CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= MaxPublishAttempts; attempt++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            // A hub that is merely down costs no attempts: this blocks until the connection
            // is usable again, so the attempt budget only counts genuine invocation failures.
            await EnsureConnectedAsync(cancellationToken);

            try
            {
                if (chunk.Length == 1)
                {
                    await connection.InvokeAsync(LogHubContract.BroadcastLog, chunk[0], cancellationToken);
                }
                else
                {
                    await connection.InvokeAsync(LogHubContract.BroadcastLogBatch, chunk, cancellationToken);
                }

                logger.LogDebug("Published {Count} entr{Suffix}.", chunk.Length, chunk.Length == 1 ? "y" : "ies");
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (attempt == MaxPublishAttempts)
                {
                    // Give up rather than spin. A chunk the hub keeps rejecting would otherwise
                    // block the tail permanently and silently, which is worse than losing it.
                    logger.LogError(ex, "Dropping {Count} entries after {Attempts} failed attempts.",
                        chunk.Length, MaxPublishAttempts);
                    return;
                }

                logger.LogWarning(ex, "Publishing {Count} entries failed (attempt {Attempt} of {Max}); retrying.",
                    chunk.Length, attempt, MaxPublishAttempts);

                await Task.Delay(TimeSpan.FromSeconds(1 << (attempt - 1)), cancellationToken);
            }
        }
    }

    /// <summary>
    /// Conservative upper bound on a message's serialised size. A UTF-16 char encodes to at
    /// most three UTF-8 bytes, plus room for the timestamp, level and property names.
    /// </summary>
    private static int EstimateBytes(LogMessage message)
    {
        return (message.Message.Length * 3) + 128;
    }

    private static LogMessage Clamp(LogMessage message)
    {
        return message.Message.Length <= MaxMessageChars
            ? message
            : message with { Message = string.Concat(message.Message.AsSpan(0, MaxMessageChars), "… [truncated]") };
    }

    /// <summary>
    /// Blocks until the connection is usable. Handles both the initial connect (state
    /// Disconnected -> StartAsync with backoff) and waiting out an automatic reconnect.
    /// </summary>
    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        TimeSpan delay = TimeSpan.FromSeconds(1);
        TimeSpan maxDelay = TimeSpan.FromSeconds(30);

        while (!cancellationToken.IsCancellationRequested && connection.State != HubConnectionState.Connected)
        {
            if (connection.State != HubConnectionState.Disconnected)
            {
                // Connecting or Reconnecting — automatic reconnect owns it, so just wait.
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
                continue;
            }

            try
            {
                await connection.StartAsync(cancellationToken);
                logger.LogInformation("Connected to LogHub (connection {ConnectionId}).", connection.ConnectionId);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("LogHub at {HubUri} is unreachable ({Reason}); retrying in {Delay}.",
                    _options.HubUri, ex.Message, delay);

                await Task.Delay(delay, cancellationToken);
                delay = TimeSpan.FromSeconds(Math.Min(maxDelay.TotalSeconds, delay.TotalSeconds * 2));
            }
        }
    }

    private void WireConnectionEvents()
    {
        connection.Reconnecting += error =>
        {
            logger.LogWarning(error, "Hub connection lost; reconnecting.");
            return Task.CompletedTask;
        };

        connection.Reconnected += connectionId =>
        {
            logger.LogInformation("Hub connection re-established ({ConnectionId}).", connectionId);
            return Task.CompletedTask;
        };

        connection.Closed += error =>
        {
            logger.LogWarning(error, "Hub connection closed.");
            return Task.CompletedTask;
        };
    }
}
