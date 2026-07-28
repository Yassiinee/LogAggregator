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
    private readonly LogSourceOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var source = SelectSource();
        logger.LogInformation("Publishing to {HubUri} from {Source}.", _options.HubUri, source.Name);

        WireConnectionEvents();

        // WithAutomaticReconnect only covers drops after a first successful connect, so the
        // initial attempt is retried here — the worker normally starts before the server is up.
        await EnsureConnectedAsync(stoppingToken);

        await foreach (var batch in source.ReadAsync(stoppingToken))
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
                var path = Path.GetFullPath(_options.FilePath);
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

    private async Task PublishAsync(IReadOnlyList<LogMessage> batch, CancellationToken cancellationToken)
    {
        for (var offset = 0; offset < batch.Count; offset += _options.MaxBatchSize)
        {
            var chunk = batch.Skip(offset).Take(_options.MaxBatchSize).ToArray();

            while (!cancellationToken.IsCancellationRequested)
            {
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
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Retry rather than drop: the source is a file we can keep reading from, and
                    // losing lines to a brief hub restart would defeat the point of the tool.
                    logger.LogWarning(ex, "Publishing {Count} entries failed; retrying.", chunk.Length);
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                }
            }
        }
    }

    /// <summary>
    /// Blocks until the connection is usable. Handles both the initial connect (state
    /// Disconnected -> StartAsync with backoff) and waiting out an automatic reconnect.
    /// </summary>
    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(1);
        var maxDelay = TimeSpan.FromSeconds(30);

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
