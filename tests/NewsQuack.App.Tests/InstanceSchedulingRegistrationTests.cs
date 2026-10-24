using NewsQuack.App.Configuration;   // NewsquackConfig / InstanceConfig / FeedConfig / DiscordTargetConfig — the registration is built from config
using NewsQuack.App.Fetching;        // IFeedFetcher — resolved by each scheduler at construction (T-010)
using NewsQuack.App.Logging;         // LogEnricher — registered Scoped, resolved per cycle/feed scope by the scheduler
using NewsQuack.App.Pipeline;        // PostPipelineRunner — the ordered stage chain (T-024)
using NewsQuack.App.Scheduling;      // InstanceScheduler + AddInstanceScheduling — the unit under test
using NewsQuack.App.Services;        // IFeedErrorTracker / FeedErrorTracker — backoff + auto-remove (T-016)
using NewsQuack.Core;                // IFeedParser / Post — the frozen contracts
using Microsoft.Extensions.DependencyInjection;   // ServiceCollection for the registration test
using Microsoft.Extensions.Hosting;   // IHostedService — asserting one scheduler per configured instance
using Microsoft.Extensions.Logging.Abstractions;   // NullLogger for the empty stage chain
using Xunit;

namespace NewsQuack.App.Tests;

/// <summary>
/// T-021 (registration semantics): AddInstanceScheduling must produce ONE hosted-service descriptor per configured instance. Since .NET 9,
/// AddHostedService(factory) is implemented as TryAddEnumerable — a second call with the same service type is silently dropped, so a naive
/// per-instance loop would register only the FIRST instance's scheduler (and none at all when another IHostedService was registered first).
/// The registration therefore uses a plain ServiceDescriptor.Add; this test pins that every instance gets its own scheduler.
/// </summary>
public class InstanceSchedulingRegistrationTests
{
    [Fact]
    public void AddInstanceScheduling_TwoInstances_RegistersOneSchedulerPerInstance()
    {
        var config = new NewsquackConfig { Instances = [
            Instance("inst-a"),   // two configured instances — the multi-instance mode is the point of the product (AGENTS.md)
            Instance("inst-b"),
        ]};

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IFeedFetcher>(new NoopFetcher());                                   // resolved at scheduler construction — never called here
        services.AddSingleton<IFeedErrorTracker>(new FeedErrorTracker(new FakeFeedStateStore(), new LogCapture().For<FeedErrorTracker>(), enableAutoRemove: false, 10));   // real tracker over the recording store (T-016)
        services.AddSingleton<IFeedParser>(new NoopParser());                                    // resolved at scheduler construction — never called here
        services.AddSingleton(new PostPipelineRunner([], NullLogger<PostPipelineRunner>.Instance));   // empty v1.0 stage chain — nothing runs in this wiring test
        services.AddScoped<LogEnricher>();                                                      // the scheduler resolves it per scope (T-004)

        services.AddInstanceScheduling(config);   // the exact wiring Program.cs uses on both host paths

        using var provider = services.BuildServiceProvider();

        var schedulers = provider.GetServices<IHostedService>().OfType<InstanceScheduler>().ToList();
        Assert.Equal(2, schedulers.Count);   // 2 instances ⇒ exactly 2 scheduler descriptors — TryAddEnumerable semantics would keep only the first
    }

    private static InstanceConfig Instance(string id) => new()
    {
        Id = id,
        CheckIntervalMinutes = 15,
        Feeds = [new FeedConfig { Url = $"https://example.com/{id}.xml" }],
        Discord = new DiscordTargetConfig { WebhookUrl = "https://discord.com/api/webhooks/123456789012345678/AbCdEfGhIjKlMnOpQrStUvWx" },
    };

    /// <summary>IFeedFetcher that never runs in this wiring test — construction only, no fetches.</summary>
    private sealed class NoopFetcher : IFeedFetcher
    {
        public Task<FeedFetchResult> FetchAsync(string feedUrl, CancellationToken ct = default) =>
            Task.FromResult(new FeedFetchResult(FetchStatus.TransportError, Error: "noop fetcher — the wiring test never fetches"));
    }

    /// <summary>IFeedParser that never runs in this wiring test — construction only, no parses.</summary>
    private sealed class NoopParser : IFeedParser
    {
        public Task<FeedParseResult> ParseAsync(string feedXml, string sourceUrl, CancellationToken ct = default) =>
            Task.FromResult(new FeedParseResult([], 0, null));
    }
}
