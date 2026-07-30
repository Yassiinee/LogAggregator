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
        await Clients.All.ReceiveLog(normalized);
    }

    /// <summary>Batch variant to reduce hub load during log floods.</summary>
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

    /// <summary>Normalizes and validates incoming messages, enforces known log levels.</summary>
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
