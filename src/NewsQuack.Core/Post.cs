namespace NewsQuack.Core;

public sealed record PostAuthor(string Name, string? Url);

public sealed record Post
{
    public required string Id { get; init; }                    // system-assigned unique post id (GUID string), assigned at construction time by the builder that creates the post
    public required string ItemId { get; init; }                // stable dedup key of the feed item (derivation rule comes later — contract only)
    public required string Title { get; init; }
    public string? Description { get; init; }                   // nullable: many feeds have no description
    public string? Link { get; init; }                          // nullable: some items have no link — code must never NRE on it
    public PostAuthor? Author { get; init; }                    // nullable: some feeds have no author
    public required DateTimeOffset PublishedAt { get; init; }   // always UTC
    public string? ImageUrl { get; init; }                      // nullable until resolved by the image stage
    public IReadOnlyList<string> Tags { get; init; } = [];      // empty, never null
    public required string SourceUrl { get; init; }             // URL of the feed this post came from
    public string? SourceName { get; init; }                    // T-053: feed/channel display name when known (RSS/RDF <channel><title>, Atom <feed><title>); null when absent or blank — consumers fall back to SourceUrl
}
