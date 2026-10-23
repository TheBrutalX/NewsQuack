using NewsQuack.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NewsQuack.App.Parsing;

/// <summary>
/// T-012: DI wiring for the feed-parsing layer, shared by both host paths in Program.cs (same pattern as AddFeedFetching).
/// One <see cref="IFeedParser"/> serves all instances/feeds — parsing is stateless and thread-safe, so a singleton suffices.
/// </summary>
public static class FeedParserRegistration
{
    public static IServiceCollection AddFeedParsing(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Pure date parser; the optional logger makes the "assumed UTC" Debug line observable in production (null in pure tests).
        services.AddSingleton(sp => new FeedDateParser(sp.GetService<ILogger<FeedDateParser>>()));

        // Later tasks (schedulers/pipeline) resolve IFeedParser — never the concrete type. The factory constructs the internal
        // parser + its stateless mapper (item data in ⇒ Post? out, identical field extraction on both passes) in this assembly.
        services.AddSingleton<IFeedParser>(sp => new FeedParser(
            sp.GetRequiredService<ILogger<FeedParser>>(),
            new FeedItemMapper(sp.GetRequiredService<FeedDateParser>())));

        return services;
    }
}
