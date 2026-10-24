using System.Globalization;
using System.Text;
using NewsQuack.App.Parsing;
using NewsQuack.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace TzCellRunner;

/// <summary>
/// T-042 matrix cell runner (coordinator decisions 1–3): a pure "parse and print" tool with zero xUnit knowledge.
/// One child process per matrix cell: the environment's TZ is fixed for the process's whole lifetime (a runtime
/// mutation of TZ does not change <see cref="TimeZoneInfo.Local"/> on net10/Linux — an in-process cell would pass
/// vacuously), and the thread culture is set at startup from the single argument.
///
/// Output contract (stdout, UTF-8, one line per parsed post, in document order):
///   &lt;fixtureName&gt;\t&lt;itemId&gt;\t&lt;title&gt;\t&lt;publishedAt "O"&gt;
/// No other stdout output. Exit code 0 on success; non-zero with a message on stderr on any failure. Deterministic:
/// same inputs ⇒ byte-identical stdout across runs and cells (that is the point of the matrix).
/// </summary>
internal static class Program
{
    // The five good fixtures, in parse order (coordinator decision 2), under tests/fixtures/ relative to the solution root.
    private static readonly string[] FixtureFiles = ["rss2.xml", "atom.xml", "youtube.xml", "reddit.xml", "gitlab.xml"];

    // fixtureName used for the inline edge-case document in the output contract.
    private const string EdgeCaseFixtureName = "edge-case.xml";

    // Inline edge-case document (coordinator decision 3): RSS 2.0, four decision-context cases across five items —
    //   A: back-dated pubDate strictly older than its siblings' dates ⇒ must keep its own older instant;
    //   B + C: two items with identical pubDate timestamps ⇒ both present at the same instant;
    //   D + E: date-only stamps on the same day (ISO and RFC 822 without time — accepted by FeedDateParser's general
    //          parse) ⇒ exact midnight-UTC instants, identical for same-day items (the midnight-bug case: many posts
    //          per day at local 00:0x are distinguished by identity, not by date).
    // All items carry distinct <guid> values and absolute links. Goes through pass 1 (CodeHollow) in every cell; the
    // mapper's own FeedDateParser result wins for every item, so library dates can never alter the output.
    private const string EdgeCaseXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0">
          <channel>
            <title>T-042 edge cases</title>
            <link>https://example.com/edge</link>
            <item>
              <title>Back-dated archive post</title>
              <link>https://example.com/edge/backdated</link>
              <guid isPermaLink="false">edge-a-backdated</guid>
              <pubDate>Tue, 14 Oct 2025 08:15:30 -0700</pubDate>
            </item>
            <item>
              <title>Sibling post one (identical timestamp)</title>
              <link>https://example.com/edge/sibling-one</link>
              <guid isPermaLink="false">edge-b-sibling-one</guid>
              <pubDate>Tue, 21 Oct 2025 09:30:00 -0700</pubDate>
            </item>
            <item>
              <title>Sibling post two (identical timestamp)</title>
              <link>https://example.com/edge/sibling-two</link>
              <guid isPermaLink="false">edge-c-sibling-two</guid>
              <pubDate>Tue, 21 Oct 2025 09:30:00 -0700</pubDate>
            </item>
            <item>
              <title>Date-only post (ISO)</title>
              <link>https://example.com/edge/date-only-iso</link>
              <guid isPermaLink="false">edge-d-date-only-iso</guid>
              <pubDate>Tue, 21 Oct 2025</pubDate>
            </item>
            <item>
              <title>Date-only post (RFC 822)</title>
              <link>https://example.com/edge/date-only-rfc822</link>
              <guid isPermaLink="false">edge-e-date-only-rfc822</guid>
              <pubDate>Tue, 21 Oct 2025</pubDate>
            </item>
          </channel>
        </rss>
        """;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 1 || !IsSupportedCulture(args[0]))
        {
            Console.Error.WriteLine("usage: TzCellRunner <cultureName> — cultureName must be one of: invariant, de-DE, it-IT");
            return 2;   // usage error ⇒ non-zero with a message on stderr
        }

        try
        {
            // Culture for the whole process lifetime (thread defaults), set before any parsing work. "invariant" is an
            // alias accepted by CultureInfo; map it explicitly to the canonical instance.
            var culture = args[0] == "invariant" ? CultureInfo.InvariantCulture : new CultureInfo(args[0]);
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;

            Console.OutputEncoding = Encoding.UTF8;   // deterministic bytes regardless of locale

            var parser = CreateParser();

            var fixturesDir = Path.Combine(FindSolutionRoot(), "tests", "fixtures");
            foreach (var file in FixtureFiles)
                await ParseAndPrintAsync(parser, File.ReadAllText(Path.Combine(fixturesDir, file), Encoding.UTF8), file);

            await ParseAndPrintAsync(parser, EdgeCaseXml, EdgeCaseFixtureName);
            return 0;   // success ⇒ exit 0
        }
        catch (Exception ex)   // any failure ⇒ non-zero with a message on stderr (never a silent empty stdout)
        {
            Console.Error.WriteLine($"TzCellRunner failed: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// The production parser, constructed exactly as the DI factory in FeedParserRegistration does (same collaborators
    /// as T-012's tests): FeedParser + FeedItemMapper + FeedDateParser. Resolved through the public registration so this
    /// runner needs no internals visibility into NewsQuack.App.
    /// </summary>
    private static IFeedParser CreateParser()
    {
        var services = new ServiceCollection();
        // Deterministic, silent logging: the only ILogger<T> services AddFeedParsing's factories resolve are these two —
        // both null loggers, so nothing can ever write to stdout/stderr and break the line contract (the parser logs at
        // Information/Warning on its fallback paths).
        services.AddSingleton<ILogger<FeedParser>>(NullLogger<FeedParser>.Instance);
        services.AddSingleton<ILogger<FeedDateParser>>(NullLogger<FeedDateParser>.Instance);
        services.AddFeedParsing();

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IFeedParser>();   // singleton constructed eagerly; the parser is stateless
    }

    private static async Task ParseAndPrintAsync(IFeedParser parser, string xml, string fixtureName)
    {
        var result = await parser.ParseAsync(xml, $"https://example.com/{fixtureName}");

        // Strict: any whole-feed error or dropped item in these fixtures is a cell failure, not something to tolerate.
        if (result.FeedError is not null)
            throw new InvalidOperationException($"{fixtureName}: whole-feed error: {result.FeedError}");
        if (result.ItemErrorCount != 0)
            throw new InvalidOperationException($"{fixtureName}: {result.ItemErrorCount} item(s) dropped; expected zero in the matrix fixtures");

        foreach (var post in result.Posts)
            Console.WriteLine($"{fixtureName}\t{post.ItemId}\t{post.Title}\t{post.PublishedAt:O}");   // document order, "O" = zero-offset UTC
    }

    private static bool IsSupportedCulture(string name) => name is "invariant" or "de-DE" or "it-IT";

    /// <summary>Walks up from the runner assembly location to the directory containing NewsQuack.sln (same approach as FixtureLoader).</summary>
    private static string FindSolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NewsQuack.sln")))
            dir = dir.Parent;

        if (dir is null)
            throw new InvalidOperationException("Could not locate NewsQuack.sln above the runner assembly location.");

        return dir.FullName;
    }
}
