using LogAggregator.Worker;
using LogAggregator.Worker.Sources;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.Services.AddOptions<LogSourceOptions>()
    .Bind(builder.Configuration.GetSection(LogSourceOptions.SectionName))
    .Validate(options =>
    {
        options.Validate();
        return true;
    })
    .ValidateOnStart();

builder.Services.AddSingleton<LogFileTailSource>();
builder.Services.AddSingleton<SimulatedLogSource>();

// One long-lived connection for the process. WithAutomaticReconnect handles drops after the
// first successful connect; Worker.EnsureConnectedAsync handles the initial attempt.
builder.Services.AddSingleton(serviceProvider =>
{
    LogSourceOptions options = serviceProvider.GetRequiredService<IOptions<LogSourceOptions>>().Value;

    return new HubConnectionBuilder()
        .WithUrl(options.HubUri)
        .WithAutomaticReconnect([
            TimeSpan.Zero,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
        ])
        .Build();
});

builder.Services.AddHostedService<Worker>();

IHost host = builder.Build();
host.Run();
