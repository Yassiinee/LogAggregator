using LogAggregator.Server.Services;
using LogAggregator.Shared;
using Microsoft.AspNetCore.SignalR;

namespace LogAggregator.Server.Hubs;

/// <summary>
/// The fan-out point of the architecture: workers invoke <see cref="BroadcastLog"/>, and every
/// connected dashboard receives the entry on <see cref="ILogClient.ReceiveLog"/>.
/// </summary>
public sealed class LogHub(LogBuffer buffer, ILogger<LogHub> logger) : Hub<ILogClient>
{
    /// <summary>
    /// Called by a worker for each new log line. Normalises the level, records it in the
    /// replay buffer, then broadcasts to all clients (including other workers, which simply
    /// never subscribe to <c>ReceiveLog</c>).
    /// </summary>
    public async Task BroadcastLog(LogMessage message)
    {
        LogMessage normalized = Normalize(message);
        buffer.Add(normalized);
        await Clients.All.ReceiveLog(normalized);
    }

    /// <summary>
    /// Batch variant, used when a tailed file produces a burst of lines at once. Sending one
    /// invocation instead of N keeps the hub from becoming the bottleneck during a log flood.
    /// </summary>
    public async Task BroadcastLogBatch(IReadOnlyList<LogMessage> messages)
    {
        if (messages is null or { Count: 0 })
        {
            return;
        }

        LogMessage[] normalized = new LogMessage[messages.Count];
        for (int i = 0; i < messages.Count; i++)
        {
            normalized[i] = Normalize(messages[i]);
        }

        buffer.AddRange(normalized);

        await Clients.All.ReceiveLogBatch(normalized);
    }

    public override async Task OnConnectedAsync()
    {
        IReadOnlyList<LogMessage> backlog = buffer.Snapshot();
        if (backlog.Count > 0)
        {
            await Clients.Caller.ReceiveLogBatch(backlog);
        }

        logger.LogInformation("Client {ConnectionId} connected; replayed {Count} entries.",
            Context.ConnectionId, backlog.Count);

        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (exception is null)
        {
            logger.LogInformation("Client {ConnectionId} disconnected.", Context.ConnectionId);
        }
        else
        {
            logger.LogWarning(exception, "Client {ConnectionId} dropped.", Context.ConnectionId);
        }

        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Never trust the producer: clamp the level to a known value and stamp a server-side
    /// timestamp if the worker sent <c>default</c>.
    /// </summary>
    private static LogMessage Normalize(LogMessage message)
    {
        return message with
        {
            Timestamp = message.Timestamp == default ? DateTime.UtcNow : message.Timestamp.ToUniversalTime(),
            LogLevel = LogLevels.Normalize(message.LogLevel),
            Message = message.Message ?? string.Empty,
        };
    }
}
