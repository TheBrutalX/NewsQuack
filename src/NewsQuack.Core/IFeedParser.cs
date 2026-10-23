namespace NewsQuack.Core;

/// <summary>Parses already-fetched feed XML; per-item error isolation happens inside the parser.</summary>
public sealed record FeedParseResult(
    IReadOnlyList<Post> Posts,     // successfully parsed items (may be empty)
    int ItemErrorCount,            // broken items dropped; each one was logged individually
    string? FeedError);            // set when the whole feed is malformed/unusable — never return a silent empty list in that case

public interface IFeedParser
{
    Task<FeedParseResult> ParseAsync(string feedXml, string sourceUrl, CancellationToken ct = default);
}
