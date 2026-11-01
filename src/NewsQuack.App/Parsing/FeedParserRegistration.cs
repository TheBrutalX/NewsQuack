using NewsQuack.App.Parsing.Builders;
using NewsQuack.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NewsQuack.App.Parsing;

/// <summary>
/// T-012: DI wiring for the feed-parsing layer, shared by both host paths in Program.cs (same pattern as AddFeedFetching).
/// One <see cref="IFeedParser"/> serves all instances/feeds — parsing is stateless and thread-safe, so a singleton suffices.
/// T-013: the parser gets an explicit <see cref="PostBuilderSelector"/> with one strategy per platform (generic / YouTube /
/// Reddit / GitLab), all built on the shared date-parser singleton. v1.0 registers every builder with a NULL description
/// limit — no config key is invented (the frozen T-002 schema is not touched).
/// </summary>
public static class FeedParserRegistration
{
    public static IServiceCollection AddFeedParsing(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Pure date parser; the optional logger makes the "assumed UTC" Debug line observable in production (null in pure tests).
        services.AddSingleton(sp => new FeedDateParser(sp.GetService<ILogger<FeedDateParser>>()));

        // Later tasks (schedulers/pipeline) resolve IFeedParser — never the concrete type. The factory constructs the internal
        // parser + its explicit platform-selector (T-013: one dedicated selection point, no platform checks elsewhere) in this
        // assembly. Only ILogger<FeedParser> and FeedDateParser are resolved here — no other service requirements.
        services.AddSingleton<IFeedParser>(sp => new FeedParser(
            sp.GetRequiredService<ILogger<FeedParser>>(),
            new PostBuilderSelector(
                new FeedItemMapper(sp.GetRequiredService<FeedDateParser>(), null),      // generic — v1.0: no description limit
                new YouTubePostBuilder(sp.GetRequiredService<FeedDateParser>(), null),  // media:thumbnail preferred for ImageUrl
                new RedditPostBuilder(sp.GetRequiredService<FeedDateParser>(), null),   // identical to generic today (single home)
                new GitLabPostBuilder(sp.GetRequiredService<FeedDateParser>(), null)))); // identical to generic today (single home)

        return services;
    }
}
