using NewsQuack.App.Configuration;
using NewsQuack.App.Fetching;             // T-010: feed fetcher + global throttle + UA cascade + robots handling
using NewsQuack.App.Health;
using NewsQuack.App.Http;                 // T-005: named HTTP clients + anti-SSRF redirect handler
using NewsQuack.App.Logging;
using NewsQuack.App.Pipeline;                // T-014: dedup engine + DedupCheckStage (Order 10); T-024: runner + v1.0 stages + send queues
using NewsQuack.App.Parsing;                 // T-012: feed parser (two-pass) + date parser + item mapper
using NewsQuack.App.Scheduling;              // T-024: per-instance scheduler (one BackgroundService per configured instance)
using NewsQuack.App.Sending;                 // T-021: per-instance send worker (one BackgroundService per configured instance)
using NewsQuack.App.Services;                // T-015: retention job
using NewsQuack.Core;                        // T-015: IStateStore contract
using NewsQuack.Infrastructure.Persistence;  // T-015: AppDbContext / SqliteStateStore / connection interceptor
using Microsoft.AspNetCore.Builder;   // WebApplication (T-030 health path) — explicit: the ASP.NET implicit usings come from the Web SDK, not this one
using Microsoft.AspNetCore.Hosting;   // ConfigureKestrel extension for the health listener
using Microsoft.EntityFrameworkCore;  // T-015: AddDbContextFactory + MigrateAsync
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

    // T-005: three named clients (feed/discord/image) with per-client timeouts from the validated config and one shared anti-SSRF redirect
    // handler in every pipeline — "discord" is what T-020's target resolves. One wiring method for both host paths.
    webBuilder.Services.AddNewsquackHttpClients(config.Http);

    webBuilder.Services.AddFeedFetching();   // T-010: the fetch layer on top of the named clients (same wiring as the generic host path)

    webBuilder.Services.AddFeedParsing();    // T-012: IFeedParser (two-pass, per-item isolation) + date parser + item mapper

    ApplyJsonLogging(webBuilder.Services, webBuilder.Logging, config);   // T-030: re-apply T-004's JSON-on-stdout setup — WebApplication's default providers would clobber it
    webBuilder.WebHost.ConfigureKestrel(options => options.ListenAnyIP(health.Port));

    AddStateStore(webBuilder.Services);   // T-015: state store + retention job (same wiring as the generic host path)

    webBuilder.Services.AddFeedErrorTracking();   // T-016: per-feed error tracker (backoff + auto-remove) — same wiring as the generic host path

    webBuilder.Services.AddDedupEngine();         // T-014: dedup engine + DedupCheckStage (Order 10) — same wiring as the generic host path

    webBuilder.Services.AddSingleton<NewsquackConfig>(config);   // T-024: the validated, immutable config — safe singleton (same wiring as the generic host path)

    webBuilder.Services.AddPostPipeline();         // T-024: runner + ImageResolutionStage + SendStage + send-queue registry (same wiring as the generic host path)

    webBuilder.Services.AddDeadLettering(config);   // T-022: dead-letter persistence + system webhook notifier — registered BEFORE the workers: their factories resolve the per-instance sink

    webBuilder.Services.AddSendWorkers(config);   // T-031 decision 1: registered BEFORE the schedulers ⇒ the host (reverse registration order) stops the schedulers first, then each worker drains its queue within the bounded window

    webBuilder.Services.AddInstanceScheduling(config);   // T-024: one per-instance scheduler — registered LAST among these ⇒ stopped FIRST on shutdown (T-031 decision 1)

    // T-015: "store" is a readiness requirement registered at wiring time — /readyz answers 503 (missing ["store"]) until the
    // startup migration succeeds. T-030 itself registers no requirements; this factory is where its own wiring declares them.
    webBuilder.Services.AddSingleton<ReadinessTracker>(sp =>
    {
        var tracker = new ReadinessTracker();
        tracker.RegisterRequirement("store");
        return tracker;
    });

    // Same per-worker enrichment registration as the generic host path, so both hosts stay equivalent for later wiring (T-004).
    webBuilder.Services.AddScoped<LogEnricher>();

    var app = webBuilder.Build();
    app.MapHealthEndpoints();

    // T-015: create/migrate the database after host build, before run — /readyz cannot pass until this succeeds.
    if (!await MigrateStoreAsync(app.Services))
        Environment.Exit(1);   // boot error contract: friendly one-liner already on stderr (see MigrateStoreAsync)

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

    // T-005: exactly the wiring of the health path above — both hosts get the three named clients with config timeouts and the shared handler.
    builder.Services.AddNewsquackHttpClients(config.Http);

    builder.Services.AddFeedFetching();   // T-010: exactly the fetch-layer wiring of the health path above

    builder.Services.AddFeedParsing();    // T-012: exactly the parsing-layer wiring of the health path above

    ApplyJsonLogging(builder.Services, builder.Logging, config);

    // Per-worker enrichment (T-004): one LogEnricher per DI scope; later tasks activate it inside their worker scopes so every
    // line emitted there carries "instanceId" / "url".
    builder.Services.AddScoped<LogEnricher>();

    AddStateStore(builder.Services);   // T-015: the store + retention job run regardless of the health listener

    builder.Services.AddFeedErrorTracking();   // T-016: per-feed error tracker — runs regardless of the health listener

    builder.Services.AddDedupEngine();         // T-014: dedup engine + DedupCheckStage (Order 10) — runs regardless of the health listener

    builder.Services.AddSingleton<NewsquackConfig>(config);   // T-024: the validated, immutable config — safe singleton (same wiring as the health path)

    builder.Services.AddPostPipeline();         // T-024: runner + ImageResolutionStage + SendStage + send-queue registry (same wiring as the health path)

    builder.Services.AddDeadLettering(config);   // T-022: dead-letter persistence + system webhook notifier — registered BEFORE the workers: their factories resolve the per-instance sink

    builder.Services.AddSendWorkers(config);   // T-031 decision 1: registered BEFORE the schedulers ⇒ the host (reverse registration order) stops the schedulers first, then each worker drains its queue within the bounded window

    builder.Services.AddInstanceScheduling(config);   // T-024: one per-instance scheduler — registered LAST among these ⇒ stopped FIRST on shutdown (T-031 decision 1)

    var host = builder.Build();

    if (!await MigrateStoreAsync(host.Services))
        Environment.Exit(1);   // boot error contract: friendly one-liner already on stderr (see MigrateStoreAsync)

    await host.RunAsync();
}

// T-015 (coordinator decision, binding): the production database — /data/newsquack.db with shared cache and a 5 s busy timeout.
const string DefaultDatabasePath = "/data/newsquack.db";

// T-015: state store wiring shared by both host paths. NEWSQUACK_DATABASE overrides the database file path (an operational env var,
// same pattern as NEWSQUACK_CONFIG — not a config key) so local runs and boot tests can point at a temp file; Microsoft.Data.Sqlite
// requires the "Data Source=" keyword, hence the explicit prefix on the assembled connection string.
static void AddStateStore(IServiceCollection services)
{
    var databasePath = Environment.GetEnvironmentVariable("NEWSQUACK_DATABASE") ?? DefaultDatabasePath;

    // Local runs without a volume: create the parent directory of the database file if missing (in Docker /data is the mount point).
    var parent = Path.GetDirectoryName(Path.GetFullPath(databasePath));
    if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
        Directory.CreateDirectory(parent);

    var connectionString = $"Data Source={databasePath};Cache=Shared;Default Timeout=5";

    // One context per operation via the factory; WAL + synchronous=NORMAL are applied by the interceptor on every connection open —
    // the one sanctioned raw-SQL exception (connection initialization, not data access).
    services.AddSingleton<SqliteConnectionInitInterceptor>();
    services.AddDbContextFactory<AppDbContext>((sp, options) =>
        options.UseSqlite(connectionString).AddInterceptors(sp.GetRequiredService<SqliteConnectionInitInterceptor>()));

    // The frozen contract — later workers/pipeline resolve IStateStore, never the concrete type.
    services.AddSingleton<IStateStore, SqliteStateStore>();

    // T-031 decision 3: WAL checkpoint on graceful shutdown — registered BEFORE the retention service so the host's reverse-order stop runs it LAST,
    // after the schedulers stopped and the send workers drained their queues. A failed checkpoint logs Error and never rethrows (it cannot crash host stop).
    services.AddHostedService<StoreCheckpointService>();

    // 24 h retention job (30-day cutoff, batch 1000) — constants per the coordinator decision, no config keys invented.
    services.AddHostedService<FeedStateRetentionService>();
}

// T-015: applies migration 001 (and any later migrations) on a factory-created context, before any worker starts. Returns false — after a
// structured error log and a friendly stderr one-liner with no stack trace (boot error contract) — when the store cannot be initialized;
// the caller exits 1. On success marks "store" ready so /readyz passes (only the health path registers that requirement).
static async Task<bool> MigrateStoreAsync(IServiceProvider services)
{
    var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("NewsQuack.App");

    try
    {
        var contextFactory = services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var context = await contextFactory.CreateDbContextAsync();   // factory-created context, per the coordinator decision
        await context.Database.MigrateAsync();                                   // before any worker starts

        if (services.GetService<ReadinessTracker>() is { } tracker)   // only the health path registers it
            tracker.MarkReady("store");

        return true;
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "State store migration failed: {Message}", ex.Message);   // structured log — exception as argument, stack trace survives
        Console.Error.WriteLine($"State store could not be initialized: {ex.Message}");   // friendly one-liner, no stack trace (boot error contract)
        return false;
    }
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
