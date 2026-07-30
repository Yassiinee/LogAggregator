using LogAggregator.Server.Services;
using LogAggregator.Shared;
using Microsoft.AspNetCore.SignalR;

namespace LogAggregator.Server.Hubs;

/// <summary>Fan-out hub for log entries: workers publish, dashboards subscribe.</summary>
public sealed class LogHub(LogBuffer buffer, ILogger<LogHub> logger) : Hub<ILogClient>
{
    /// <summary>Records and broadcasts a single log message.</summary>
    public async Task BroadcastLog(LogMessage message)
    {
        LogMessage normalized = Normalize(message);
        buffer.Add(normalized);
        // Don't echo back to the producer; only send to dashboards.
        await Clients.AllExcept(Context.ConnectionId).ReceiveLog(normalized);
    }

    /// <summary>Batch variant to reduce hub load during log floods.</summary>
    public async Task BroadcastLogBatch(IReadOnlyList<LogMessage> messages)
    {
        if (messages is null or { Count: 0 })
        {
            return;
        }

        // Clamp batch size to prevent abuse.
        int count = Math.Min(messages.Count, 1000);

        LogMessage[] normalized = new LogMessage[count];
        for (int i = 0; i < count; i++)
        {
            normalized[i] = Normalize(messages[i]);
        }

        buffer.AddRange(normalized);

        // Don't echo back to the producer; only send to dashboards.
        await Clients.AllExcept(Context.ConnectionId).ReceiveLogBatch(normalized);
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

    /// <summary>Normalizes and validates incoming messages, enforces known log levels.</summary>
    private static LogMessage Normalize(LogMessage message)
    {
        // Clamp message length to prevent excessive memory usage.
        const int MaxMessageLength = 32 * 1024;
        string msg = message.Message ?? string.Empty;
        if (msg.Length > MaxMessageLength)
        {
            msg = msg[..MaxMessageLength] + "… [truncated]";
        }

        return new LogMessage(
            message.Timestamp == default ? DateTime.UtcNow : message.Timestamp.ToUniversalTime(),
            LogLevels.Normalize(message.LogLevel),
            msg);
    }
}
