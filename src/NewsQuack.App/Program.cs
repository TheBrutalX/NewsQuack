using NewsQuack.App.Configuration;
using NewsQuack.App.Logging;
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

// T-004: from here on, all logging is structured JSON — one line per entry on stdout (frozen format, see tasks/t-004).
var builder = Host.CreateApplicationBuilder(args);

// Replace the default console/debug providers with the JSON line provider and honor Logging.Level from the validated config.
// SetMinimumLevel is the host's standard level-configuration mechanism — no custom filtering code in the provider itself.
builder.Logging.ClearProviders();
builder.Logging.SetMinimumLevel(JsonLoggerProvider.ParseLevel(configResult.Config?.Logging?.Level));
builder.Services.AddSingleton<ILoggerProvider>(new JsonLoggerProvider(Console.Out));

// Per-worker enrichment (T-004): one LogEnricher per DI scope; later tasks activate it inside their worker scopes so every
// line emitted there carries "instanceId" / "url".
builder.Services.AddScoped<LogEnricher>();

await builder.Build().RunAsync();
