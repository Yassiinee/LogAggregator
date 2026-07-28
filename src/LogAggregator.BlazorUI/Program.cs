using LogAggregator.BlazorUI;
using LogAggregator.BlazorUI.Components;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddOptions<LogHubOptions>()
    .Bind(builder.Configuration.GetSection(LogHubOptions.SectionName))
    .Validate(
        options => Uri.TryCreate(options.ServerBaseUrl, UriKind.Absolute, out _),
        $"{LogHubOptions.SectionName}:{nameof(LogHubOptions.ServerBaseUrl)} must be an absolute URL.")
    .ValidateOnStart();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
