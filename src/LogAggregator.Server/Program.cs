using LogAggregator.Server.Hubs;
using LogAggregator.Server.Services;
using LogAggregator.Shared;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddSignalR(options =>
{
    // A dashboard that stops responding should be dropped reasonably quickly.
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
    options.KeepAliveInterval = TimeSpan.FromSeconds(10);
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();

    // The framework default is 32 KB, which one stack trace can exceed. The hub then drops
    // the connection mid-invocation and the producer republishes the same oversized frame,
    // so set the ceiling deliberately — the worker chunks well under it.
    options.MaximumReceiveMessageSize = 1024 * 1024;
});

// Replay history for late-joining dashboards.
int backlogSize = builder.Configuration.GetValue("LogHub:BacklogSize", 500);
if (backlogSize < 0)
{
    throw new InvalidOperationException(
        $"LogHub:BacklogSize must be zero or greater (got {backlogSize}). Zero disables replay.");
}

builder.Services.AddSingleton(new LogBuffer(backlogSize));

// Only needed if a browser-hosted client (Blazor WebAssembly) talks to the hub directly.
// With the Blazor Server render mode the HubConnection lives in the UI process, so it never
// goes through CORS — configured here so switching render modes does not break the app.
const string BlazorCorsPolicy = "BlazorClients";
string[] allowedOrigins = builder.Configuration.GetSection("LogHub:AllowedOrigins").Get<string[]>()
    ?? ["https://localhost:7260", "http://localhost:5114"];

builder.Services.AddCors(options => options.AddPolicy(BlazorCorsPolicy, policy => policy
    .WithOrigins(allowedOrigins)
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials())); // SignalR requires credentials for its negotiate handshake.

WebApplication app = builder.Build();

app.UseCors(BlazorCorsPolicy);

app.MapHub<LogHub>(LogHubContract.Path);

app.MapGet("/", () => Results.Ok(new
{
    service = "LogAggregator.Server",
    hub = LogHubContract.Path,
    status = "up",
}));

app.Run();
