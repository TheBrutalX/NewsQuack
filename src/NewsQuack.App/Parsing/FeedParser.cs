using System.Xml.Linq;
using CodeHollow.FeedReader;
using NewsQuack.App.Parsing.Builders;
using NewsQuack.Core;
using Microsoft.Extensions.Logging;

namespace NewsQuack.App.Parsing;

/// <summary>
/// T-012: implements the frozen <see cref="IFeedParser"/> contract with per-item error isolation and strict UTC
/// date normalization. Two passes produce the same <see cref="Post"/> mapping for the same item:
///   • Pass 1 (primary): CodeHollow <c>FeedReader.ReadFromString</c>. A single bad Atom date makes this throw for the
///     whole document, so it cannot by itself guarantee per-item isolation.
///   • Pass 2 (tolerant fallback): runs only when pass 1 throws — walks the raw XML node-by-node so one broken item is
///     dropped without taking down the feed.
/// A whole-feed failure always surfaces as an observable <c>FeedError</c> — never a silent empty list.
/// T-013: per-item mapping is delegated to a per-platform <see cref="IPostBuilder"/> strategy, selected ONCE per
/// <see cref="ParseAsync"/> call by the single <see cref="PostBuilderSelector"/> (the source URL is constant per feed).
/// </summary>
public sealed class FeedParser : IFeedParser
{
    private readonly ILogger<FeedParser> _logger;
    private readonly PostBuilderSelector _selector;

    // Internal constructors: the class is public (it implements the frozen IFeedParser contract) but its collaborators are
    // internal, so DI constructs it via a factory in the same assembly rather than through an exposed public constructor.
    internal FeedParser(ILogger<FeedParser> logger, PostBuilderSelector selector)
    {
        _logger = logger;
        _selector = selector;
    }

    // T-012 test-facing constructor: the given mapper becomes the generic strategy of a default selector (the platform
    // strategies are constructed around it). Keeps T-012's tests compiling and behaving byte-identically.
    internal FeedParser(ILogger<FeedParser> logger, FeedItemMapper mapper)
        : this(logger, new PostBuilderSelector(mapper)) { }

    /// <inheritdoc />
    /// <remarks>
    /// The work is synchronous (parse + map); it runs on the thread pool so a large feed never blocks the caller's
    /// context, and the token is honored both for scheduling and between items. No sync-over-async anywhere.
    /// </remarks>
    public Task<FeedParseResult> ParseAsync(string feedXml, string sourceUrl, CancellationToken ct = default) =>
        Task.Run(() => ParseCore(feedXml, sourceUrl, ct), ct);

    private FeedParseResult ParseCore(string feedXml, string sourceUrl, CancellationToken ct)
    {
        // ── Pass 1: primary parser. A whole-document throw (bad Atom date, malformed XML, unsupported type) means this
        // pass cannot isolate items — fall through to the tolerant pass. Cancellation is not a feed error. ──
        Feed? feed = null;
        try
        {
            feed = FeedReader.ReadFromString(feedXml);   // static; throws on whole-feed problems (see coordinator decision 2)
        }
        catch (OperationCanceledException)
        {
            throw;   // not expected from ReadFromString, but never swallow a cancellation as a feed error
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Primary parser failed for {SourceUrl}; using tolerant fallback", sourceUrl);
        }

        ct.ThrowIfCancellationRequested();   // between passes

        if (feed is null)
            return MapFeedItems(feedXml, sourceUrl, libraryItems: null, ct);   // pass 2: no library dates available

        return MapFeedItems(feedXml, sourceUrl, feed.Items, ct);                // pass 1: library dates as per-item fallback
    }

    /// <summary>
    /// Tolerant pass exposed for tests (and reused by <see cref="ParseCore"/>): parses the raw XML and maps each item
    /// node with per-item isolation. No primary-parser dates are available here, so a date is kept only if our own parser
    /// can read it — never "now".
    /// </summary>
    internal FeedParseResult ParseTolerantPass(string feedXml, string sourceUrl) =>
        MapFeedItems(feedXml, sourceUrl, libraryItems: null, CancellationToken.None);

    // ── Shared item walk + mapping (identical extraction for both passes) ────────────────────────────────────────────

    private FeedParseResult MapFeedItems(string feedXml, string sourceUrl, IList<FeedItem>? libraryItems, CancellationToken ct)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(feedXml);   // throws XmlException on a truncated/malformed document ⇒ whole-feed error
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)   // structured: template + exception as argument so the stack trace survives
        {
            _logger.LogWarning(ex, "Feed XML could not be parsed: {Message}", FirstLine(ex.Message));
            return WholeFeedError(ex);
        }

        var root = doc.Root!;   // ! safe: XDocument.Parse only returns a document with a non-null root element
        var isAtom = root.Name.LocalName == "feed";
        var container = isAtom ? root : (Child(root, "channel") ?? root);   // feed-level author fallback target

        // Namespace-aware by local name: <item> in RSS/RDF, <entry> in Atom. A document is one or the other, so the union
        // never double-collects. LocalName ignores the default namespace (Atom feeds declare one).
        var nodes = doc.Descendants()
            .Where(e => e.Name.LocalName is "item" or "entry")
            .ToList();

        if (nodes.Count == 0)
        {
            _logger.LogWarning("Feed at {SourceUrl} is well-formed but contains no item/entry nodes", sourceUrl);   // never a silent empty list
            return new FeedParseResult([], 0, "no items found");
        }

        // T-013: the platform strategy is selected ONCE per ParseAsync call — the source URL is constant for this feed, so
        // every item of it maps through the same builder. Selection never throws (unparseable ⇒ generic).
        var builder = _selector.Select(sourceUrl);

        var posts = new List<Post>(nodes.Count);
        var itemErrors = 0;

        for (var i = 0; i < nodes.Count; i++)
        {
            ct.ThrowIfCancellationRequested();   // honor the token between items (cheap check)

            var raw = BuildRawItem(nodes[i], container, isAtom, libraryItems is not null ? LibraryDateAt(libraryItems, i) : null);

            try
            {
                if (builder.Build(raw, sourceUrl) is { } post)   // null only if a builder ever returns it; drops throw instead
                    posts.Add(post);
            }
            catch (ItemDropException drop)   // known droppable case: missing title/date — no exception object to attach
            {
                itemErrors++;
                _logger.LogWarning("Dropping feed item from {SourceUrl}: {Reason} ({ItemRef})", sourceUrl, drop.Reason, ItemRef(raw));
            }
            catch (Exception ex)   // any other fault during this item's mapping — isolate it to the item; exception as argument
            {
                itemErrors++;
                _logger.LogWarning(ex, "Unexpected error mapping feed item from {SourceUrl}; dropping it ({ItemRef})", sourceUrl, ItemRef(raw));
            }
        }

        return new FeedParseResult(posts, itemErrors, null);
    }

    /// <summary>Builds the per-item log reference (guid/id or link when known) for structured drop logging.</summary>
    private static string ItemRef(RawFeedItem raw) => FirstNonEmpty(raw.GuidOrId, raw.Link) ?? "(no id/link)";

    private static RawFeedItem BuildRawItem(XElement node, XElement container, bool isAtom, DateTimeOffset? libraryDate)
    {
        var title = Child(node, "title")?.Value;
        var link = isAtom ? AtomLink(node) : Child(node, "link")?.Value;
        var guidOrId = FirstNonEmpty(Child(node, "guid")?.Value, Child(node, "id")?.Value);

        // Date source: prefer the canonical item date (published / pubDate); fall back to updated (Atom only).
        var rawDate = isAtom
            ? FirstNonEmpty(Child(node, "published")?.Value, Child(node, "updated")?.Value)
            : Child(node, "pubDate")?.Value;

        // Description: first non-empty candidate element of the feed type. Kept as raw HTML text (one XML-decode level) — inline
        // elements such as <img> are NOT part of .Value, so the image is pulled from the element separately for ImageUrl.
        var descriptionElement = isAtom
            ? FirstNonEmptyElement(node, "summary", "content")
            : FirstNonEmptyElement(node, "description", "encoded");   // content:encoded → local name "encoded"

        var description = descriptionElement?.Value;
        var rawImageSrc = FirstImgSrc(descriptionElement);   // first real <img> inside the description (unresolved)

        // T-013: <media:group>/<media:thumbnail @url>, namespace-aware by local name (YouTube's media namespace); null when absent.
        var mediaThumbnailUrl = Child(node, "group")?   // local name of <media:group> — the prefix is ignored by design
            .Elements()
            .FirstOrDefault(e => e.Name.LocalName == "thumbnail")?
            .Attribute("url")?.Value;

        var author = ExtractAuthor(node, container, isAtom);

        return new RawFeedItem(title, link, guidOrId, rawDate, libraryDate, description, rawImageSrc, mediaThumbnailUrl, author);
    }

    /// <summary>
    /// Author from the item's own element (not CodeHollow's unreliable <c>FeedItem.Author</c> string): Atom &lt;author&gt; ⇒
    /// name + uri; RSS dc:creator ⇒ name only. Falls back to the feed/channel-level author, else null (decision 4).
    /// </summary>
    private static PostAuthor? ExtractAuthor(XElement node, XElement container, bool isAtom)
    {
        if (isAtom)
            return AtomAuthor(Child(node, "author")) ?? AtomAuthor(Child(container, "author"));

        var creator = Child(node, "creator");   // dc:creator → local name "creator"
        if (!string.IsNullOrWhiteSpace(creator?.Value))
            return new PostAuthor(creator.Value.Trim(), null);   // safe: a non-blank creator?.Value implies creator itself is non-null

        var channelCreator = Child(container, "creator");   // channel-level fallback
        if (!string.IsNullOrWhiteSpace(channelCreator?.Value))
            return new PostAuthor(channelCreator.Value.Trim(), null);   // safe: same reasoning as above

        return null;
    }

    private static PostAuthor? AtomAuthor(XElement? authorElement)
    {
        if (authorElement is null) return null;

        var name = Child(authorElement, "name")?.Value;
        if (string.IsNullOrWhiteSpace(name)) return null;   // no usable name ⇒ treat the author as absent

        var uri = Child(authorElement, "uri")?.Value;
        return new PostAuthor(name.Trim(), string.IsNullOrWhiteSpace(uri) ? null : uri.Trim());   // narrowed non-null in the false branch
    }

    /// <summary>Atom &lt;link&gt;: prefer rel="alternate" (or no rel); otherwise the first link's href.</summary>
    private static string? AtomLink(XElement node)
    {
        XElement? fallback = null;
        foreach (var element in node.Elements())
        {
            if (element.Name.LocalName != "link") continue;

            var rel = element.Attribute("rel")?.Value;
            if (rel is null or "alternate") return element.Attribute("href")?.Value;

            fallback ??= element;   // remember the first link in case no alternate is present
        }

        return fallback?.Attribute("href")?.Value;
    }

    private static DateTimeOffset? LibraryDateAt(IList<FeedItem> items, int index)
    {
        if (index >= items.Count) return null;   // defensive: primary parser and raw walk should agree on count/order

        var date = items[index].PublishingDate;   // DateTime?, already Kind=Utc when CodeHollow set it
        return date is { } d ? new DateTimeOffset(d.ToUniversalTime(), TimeSpan.Zero) : null;
    }

    private static FeedParseResult WholeFeedError(Exception ex) =>
        new([], 0, $"{ex.GetType().Name}: {FirstLine(ex.Message)}");   // observable error carrying the exception type name

    private static string FirstLine(string message)
    {
        var index = message.IndexOfAny(['\r', '\n']);
        return index < 0 ? message : message[..index];
    }

    private static XElement? Child(XElement parent, string localName) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName);   // direct child, namespace-agnostic

    /// <summary>First direct child (by local name, in the given priority order) whose text is non-blank.</summary>
    private static XElement? FirstNonEmptyElement(XElement parent, params string[] localNames) =>
        localNames.Select(name => Child(parent, name)).FirstOrDefault(e => e is { } el && !string.IsNullOrWhiteSpace(el.Value));

    /// <summary>src attribute of the first real &lt;img&gt; descendant of the element (namespace-agnostic), or null when none.</summary>
    private static string? FirstImgSrc(XElement? element) =>
        element?.Descendants().FirstOrDefault(e => e.Name.LocalName == "img")?.Attribute("src")?.Value;

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
            if (!string.IsNullOrWhiteSpace(value)) return value;

        return null;
    }
}
