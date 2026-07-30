using System.Runtime.CompilerServices;
using LogAggregator.Shared;
using Microsoft.Extensions.Options;

namespace LogAggregator.Worker.Sources;

/// <summary>Generates simulated log traffic with weighted log levels for testing.</summary>
internal sealed class SimulatedLogSource(
    IOptions<LogSourceOptions> options,
    ILogger<SimulatedLogSource> logger) : ILogSource
{
    private static readonly string[] InfoTemplates =
    [
        "GET /api/orders responded 200 in {0} ms",
        "Order {1} accepted for customer {2}",
        "Cache hit ratio steady at {3}%",
        "Health probe succeeded for node-{4}",
        "Published 1 message to queue 'orders.created'",
    ];

    private static readonly string[] WarningTemplates =
    [
        "GET /api/orders responded 200 in {0} ms (above the 250 ms budget)",
        "Retrying payment for order {1} (attempt 2 of 3)",
        "Connection pool at {3}% utilisation",
        "Clock drift of {0} ms detected on node-{4}",
    ];

    private static readonly string[] ErrorTemplates =
    [
        "Unhandled SqlException while committing order {1}: deadlock victim",
        "Payment gateway timed out after {0} ms for order {1}",
        "Node-{4} failed its readiness probe 3 consecutive times",
        "Deserialisation of message {1} failed: unexpected token",
    ];

    private readonly LogSourceOptions _options = options.Value;

    public string Name => $"simulator (every {_options.SimulationIntervalMilliseconds} ms)";

    public async IAsyncEnumerable<IReadOnlyList<LogMessage>> ReadAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        TimeSpan interval = TimeSpan.FromMilliseconds(Math.Max(25, _options.SimulationIntervalMilliseconds));
        logger.LogInformation("Simulating log traffic every {Interval}.", interval);

        using PeriodicTimer timer = new(interval);

        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            yield return [Next()];
        }
    }

    private static LogMessage Next()
    {
        int roll = Random.Shared.Next(100);
        (string? level, string[]? templates) = roll switch
        {
            < 10 => (LogLevels.Error, ErrorTemplates),
            < 30 => (LogLevels.Warning, WarningTemplates),
            _ => (LogLevels.Info, InfoTemplates),
        };

        string template = templates[Random.Shared.Next(templates.Length)];
        string text = string.Format(
            template,
            Random.Shared.Next(8, 900),              // {0} duration in ms
            Random.Shared.Next(10_000, 99_999),      // {1} order id
            Random.Shared.Next(1_000, 9_999),        // {2} customer id
            Random.Shared.Next(40, 99),              // {3} percentage
            Random.Shared.Next(1, 6));               // {4} node number

        return new LogMessage(DateTime.UtcNow, level, text);
    }
}
