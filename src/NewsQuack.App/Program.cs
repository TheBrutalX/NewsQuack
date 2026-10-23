using NewsQuack.App.Configuration;
using NewsQuack.App.Health;
using NewsQuack.App.Logging;
using Microsoft.AspNetCore.Builder;   // WebApplication (T-030 health path) — explicit: the ASP.NET implicit usings come from the Web SDK, not this one
using Microsoft.AspNetCore.Hosting;   // ConfigureKestrel extension for the health listener
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// T-002: load + validate the v2 config before anything else runs. On any failure, print each message to stderr
// (one per line) and exit with code 1 — never throw out of startup, never print a stack trace.
var searchPaths = ConfigLoader.GetSearchPaths(args, () => Environment.GetEnvironmentVariable(ConfigLoader.ConfigEnvironmentVariable));

var configResult = ConfigLoader.FindFirstExisting(searchPaths) is { } configPath
    ? ConfigLoader.LoadAndValidate(configPath)
    : ConfigLoader.MissingFile(searchPaths);

if (!configResult.IsSuccess)
{
    // Boot error path (T-002/T-003): human-readable stderr, byte-identical to before — the JSON logging pipeline does not
    // exist yet at this point and must never receive these messages.
    foreach (var message in configResult.Errors)
        Console.Error.WriteLine(message);

    Environment.Exit(1);
}

// ! safe: IsSuccess implies a parsed, valid document (ConfigLoadResult contract) — no silent fallback.
var config = configResult.Config!;

// T-030: schema defaults apply when the section is omitted (HealthSectionConfig's initializers: Enabled=true, Port=8081).
var health = config.Health ?? new HealthSectionConfig();

if (health.Enabled)
{
    // T-030: minimal-API WebApplication with Kestrel bound to all interfaces on Health.Port — the container is fronted by a
    // reverse proxy/Portainer. No HTTPS, no auth (non-goals).
    var webBuilder = WebApplication.CreateBuilder(args);

    ApplyJsonLogging(webBuilder.Services, webBuilder.Logging, config);   // T-030: re-apply T-004's JSON-on-stdout setup — WebApplication's default providers would clobber it
    webBuilder.WebHost.ConfigureKestrel(options => options.ListenAnyIP(health.Port));

    // T-030 registers no requirements — /readyz passes as soon as the listener is up (config validity is implied: we only reach
    // Kestrel after validation). Later tasks register + mark their own components; they never modify this wiring file.
    webBuilder.Services.AddSingleton<ReadinessTracker>();

    // Same per-worker enrichment registration as the generic host path, so both hosts stay equivalent for later wiring (T-004).
    webBuilder.Services.AddScoped<LogEnricher>();

    var app = webBuilder.Build();
    app.MapHealthEndpoints();

    try
    {
        await app.RunAsync();
    }
    catch (IOException ex)
    {
        // Kestrel surfaces a bind failure (port already in use) as an IOException whose message names the address. Convert it fully to a friendly one-liner — same contract as the config boot error: no stack trace, exit 1.
        Console.Error.WriteLine($"Health listener could not start on port {health.Port}: {ex.Message}");
        Environment.Exit(1);
    }
}
else
{
    // T-030: Health.Enabled=false — exactly the generic host from before, with no HTTP listener at all.

    // T-004: from here on, all logging is structured JSON — one line per entry on stdout (frozen format, see tasks/t-004).
    var builder = Host.CreateApplicationBuilder(args);

    ApplyJsonLogging(builder.Services, builder.Logging, config);

    // Per-worker enrichment (T-004): one LogEnricher per DI scope; later tasks activate it inside their worker scopes so every
    // line emitted there carries "instanceId" / "url".
    builder.Services.AddScoped<LogEnricher>();

    await builder.Build().RunAsync();
}

// T-030/T-004: the JSON-on-stdout logging setup shared by both host paths, so behavior is identical on each.
static void ApplyJsonLogging(IServiceCollection services, ILoggingBuilder logging, NewsquackConfig config)
{
    // Replace the default console/debug providers with the JSON line provider and honor Logging.Level from the validated config.
    // SetMinimumLevel is the host's standard level-configuration mechanism — no custom filtering code in the provider itself.
    logging.ClearProviders();
    logging.SetMinimumLevel(JsonLoggerProvider.ParseLevel(config.Logging?.Level));
    services.AddSingleton<ILoggerProvider>(new JsonLoggerProvider(Console.Out));   // exactly T-004's registration (singleton ILoggerProvider instance)
}
