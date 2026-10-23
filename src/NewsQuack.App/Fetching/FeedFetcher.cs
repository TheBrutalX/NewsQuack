using System.Collections.Concurrent;
using System.Text;
using NewsQuack.App.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NewsQuack.App.Fetching;

/// <summary>
/// T-010: the feed-fetching layer. GETs feed URLs through the named "feed" client (T-005 wiring: per-client timeout, SSRF-safe manual
/// redirects, native gzip) under the single global balanced throttle — every request this component makes (feed GETs AND robots.txt fetches)
/// waits on it and releases in a finally on every path. Per origin it applies robots.txt rules (tolerant parser, fail open on 404/failure,
/// decision cached for the process lifetime); per URL it resolves the User-Agent through the validated fallback cascade (on 401/403 the next
/// candidate is tried, each at most once; winners are cached in a bounded LRU). The result is always an observable <see cref="FeedFetchResult"/> —
/// never a silent empty or swallowed failure. One instance serves all instances/feeds: multi-instance mode shares the global throttle by design.
/// </summary>
public sealed class FeedFetcher : IFeedFetcher
{
    /// <summary>robots.txt bodies larger than this many bytes are not parsed — fail open with a Warning (coordinator decision, "e.g. 1 MB").</summary>
    public const int MaxRobotsBytes = 1024 * 1024;

    private readonly HttpClient _http;
    private readonly GlobalHttpThrottle _throttle;
    private readonly List<string?> _userAgents;   // the validated cascade in retry order; a single null entry when no candidate survived validation
    private readonly UaCache _uaCache;
    private readonly ConcurrentDictionary<string, bool> _robotsAllowedByOrigin = new(StringComparer.OrdinalIgnoreCase);   // origin → allowed, process lifetime (origins are few)
    private readonly ILogger<FeedFetcher> _logger;

    public FeedFetcher(IHttpClientFactory httpFactory, GlobalHttpThrottle throttle, UserAgentResolver userAgentResolver, UaCache? uaCache = null, ILogger<FeedFetcher>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(httpFactory);
        ArgumentNullException.ThrowIfNull(throttle);
        ArgumentNullException.ThrowIfNull(userAgentResolver);

        _http = httpFactory.CreateClient(NamedHttpClientRegistration.FeedClientName);   // always the named "feed" client — never a raw HttpClient
        _throttle = throttle;
        _userAgents = userAgentResolver.Candidates.Count > 0 ? new List<string?>(userAgentResolver.Candidates) : new List<string?> { null };   // no valid candidate → one request without a User-Agent header, still observable
        _uaCache = uaCache ?? new UaCache();   // default cap 1000 — process lifetime, per URL
        _logger = logger ?? NullLogger<FeedFetcher>.Instance;
    }

    public async Task<FeedFetchResult> FetchAsync(string feedUrl, CancellationToken ct = default)
    {
        Uri uri;
        try
        {
            uri = new Uri(feedUrl);
        }
        catch (UriFormatException ex)   // a malformed URL is observable state — never a throw out of the fetcher, never a silent empty result
        {
            _logger.LogWarning(ex, "Feed URL is not a valid URI: '{FeedUrl}'", feedUrl);
            return new FeedFetchResult(FetchStatus.TransportError, Error: $"not a valid URI: {feedUrl}");
        }

        if (uri.Scheme is not ("http" or "https"))
        {
            _logger.LogWarning("Feed URL scheme '{Scheme}' must be http or https: '{FeedUrl}'", uri.Scheme, feedUrl);
            return new FeedFetchResult(FetchStatus.TransportError, Error: $"scheme '{uri.Scheme}' must be http or https");
        }

        if (!await IsAllowedByRobotsAsync(uri, ct).ConfigureAwait(false))
            return new FeedFetchResult(FetchStatus.RobotsBlocked, Error: "feed path is disallowed by robots.txt");

        // UA cascade: start directly with the cached winner for this URL when there is one (subsequent fetches never re-try rejected candidates);
        // each candidate is tried at most once per call.
        var urlKey = uri.AbsoluteUri;
        var startIndex = 0;
        if (_uaCache.TryGet(urlKey, out var cachedUserAgent))
            startIndex = _userAgents.IndexOf(cachedUserAgent);   // ≥ 0: the cache only ever holds one of our own candidates

        int? lastStatus = null;

        for (var i = startIndex; i < _userAgents.Count; i++)
        {
            var userAgent = _userAgents[i];
            FeedFetchResult? mapped;   // null → the candidate was rejected with 401/403, try the next one

            try
            {
                using var response = await SendAsync(uri, userAgent, ct).ConfigureAwait(false);
                lastStatus = (int)response.StatusCode;

                if (lastStatus is 401 or 403)
                    continue;   // rejected candidate — cascade to the next one; the body is irrelevant here

                CacheWinnerIfNew(urlKey, userAgent, i);

                mapped = response.IsSuccessStatusCode
                    ? new FeedFetchResult(FetchStatus.Ok, Content: await ReadUtf8Async(response.Content, ct).ConfigureAwait(false), StatusCode: lastStatus)
                    : new FeedFetchResult(FetchStatus.HttpError, StatusCode: lastStatus, Error: $"HTTP {lastStatus}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;   // caller cancellation propagates — never mapped to a status (coordinator decision)
            }
            catch (OperationCanceledException ex)
            {
                // The client's own per-client timeout fired (T-005) without the caller cancelling → TaskCanceledException.
                _logger.LogWarning(ex, "Feed request timed out: {FeedUrl}", urlKey);
                return new FeedFetchResult(FetchStatus.Timeout, StatusCode: lastStatus, Error: "request timed out");
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Feed request failed: {FeedUrl}", urlKey);
                return new FeedFetchResult(FetchStatus.TransportError, StatusCode: lastStatus, Error: $"transport failure: {ex.Message}");
            }
            catch (Exception ex)   // any other transport-level surprise (socket errors surface here when not wrapped) — observable, never silent
            {
                _logger.LogError(ex, "Feed request failed unexpectedly: {FeedUrl}", urlKey);
                return new FeedFetchResult(FetchStatus.TransportError, StatusCode: lastStatus, Error: $"unexpected transport failure: {ex.Message}");
            }

            return mapped!;   // ! safe: the try block assigns on every non-continue path (401/403 continue before it)
        }

        // Every candidate was rejected with 401/403 — return the last error (coordinator decision).
        _logger.LogWarning("User-Agent cascade exhausted for feed {FeedUrl}: all {Count} candidate(s) rejected, last status {Status}", urlKey, _userAgents.Count - startIndex, lastStatus);
        return new FeedFetchResult(FetchStatus.HttpError, StatusCode: lastStatus, Error: $"HTTP {lastStatus}");
    }

    /// <summary>One GET through the named "feed" client under the global throttle — wait before, release in finally on EVERY path (success, HTTP
    /// error, transport error, cancellation), so AvailableCount always returns to its initial value. The response is owned by the caller.</summary>
    private async Task<HttpResponseMessage> SendAsync(Uri uri, string? userAgent, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);

        if (userAgent is not null)
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);   // candidates are validated at construction — this never throws for them

        await _throttle.WaitAsync(ct).ConfigureAwait(false);   // caller cancellation here throws BEFORE taking a slot → nothing to release
        try
        {
            return await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        finally
        {
            _throttle.Release();   // balanced on every path — the R8 property (T-041 pins it down later)
        }
    }

    /// <summary>robots.txt gate for the feed's origin (coordinator decision): fetched from scheme://host/robots.txt through the same throttle +
    /// "feed" client with candidate #1, and the DECISION is cached per origin for the process lifetime. 404 or any fetch failure fails OPEN
    /// (proceed); only a parsed "disallowed" blocks. Caller cancellation propagates; everything else is logged and becomes an allowed decision.</summary>
    private async Task<bool> IsAllowedByRobotsAsync(Uri feedUri, CancellationToken ct)
    {
        var origin = OriginKey(feedUri);

        if (_robotsAllowedByOrigin.TryGetValue(origin, out var cached))
        {
            _logger.LogDebug("robots decision for origin {Origin} (cached): {Decision}", origin, DecisionName(cached));
            return cached;
        }

        var robotsUri = new UriBuilder(feedUri) { Path = "/robots.txt", Query = string.Empty }.Uri;   // same origin — default port normalized away
        bool allowed;

        try
        {
            using var response = await SendAsync(robotsUri, _userAgents[0], ct).ConfigureAwait(false);   // candidate #1 (null when none survived validation)

            if ((int)response.StatusCode == 404)
            {
                _logger.LogDebug("No robots.txt at {RobotsUrl} (HTTP 404) — failing open", robotsUri);
                allowed = true;
            }
            else if (response.IsSuccessStatusCode)
            {
                var text = await ReadCappedAsync(response.Content, MaxRobotsBytes, ct).ConfigureAwait(false);

                if (text is null)
                {
                    _logger.LogWarning("robots.txt at {RobotsUrl} exceeds the {MaxBytes}-byte cap — failing open", robotsUri, MaxRobotsBytes);
                    allowed = true;
                }
                else
                {
                    var parsed = RobotsParser.Parse(text, feedUri.AbsolutePath);   // the path component only — never the query string

                    if (parsed.MalformedLineCount > 0)
                        _logger.LogWarning("robots.txt at {RobotsUrl} contained {MalformedLines} malformed line(s) without ':' — skipped", robotsUri, parsed.MalformedLineCount);

                    allowed = parsed.Allowed;
                }
            }
            else
            {
                _logger.LogWarning("robots.txt fetch from {RobotsUrl} returned HTTP {StatusCode} — failing open", robotsUri, (int)response.StatusCode);
                allowed = true;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // caller cancellation during the robots fetch propagates exactly like during a feed request
        }
        catch (Exception ex)   // transport failure, client timeout, anything unexpected — fail open, exception as the logger argument
        {
            _logger.LogWarning(ex, "robots.txt fetch from {RobotsUrl} failed — failing open", robotsUri);
            allowed = true;
        }

        _robotsAllowedByOrigin[origin] = allowed;   // process-lifetime cache — fail-open decisions included (the RESULT is what gets cached)
        _logger.LogDebug("robots decision for origin {Origin}: {Decision}", origin, DecisionName(allowed));
        return allowed;
    }

    /// <summary>Caches the winning candidate for this URL when it was not already cached (or changed) — Info log per newly-cached winner. A hit
    /// that re-confirms the cached winner is not logged again (it would fire on every fetch).</summary>
    private void CacheWinnerIfNew(string urlKey, string? userAgent, int index)
    {
        if (userAgent is null) return;   // nothing valid to cache when no candidate survived validation

        if (_uaCache.TryGet(urlKey, out var existing) && existing == userAgent)
            return;

        _uaCache.Set(urlKey, userAgent);
        _logger.LogInformation("Cached User-Agent winner '{UserAgent}' (candidate {Index}) for feed {FeedUrl}", userAgent, index + 1, urlKey);
    }

    /// <summary>The body as UTF-8 text — bytes are decoded explicitly: ReadAsStringAsync would fall back to ISO-8859-1 when the Content-Type
    /// carries no charset, and feeds must come out as UTF-8 (coordinator decision).</summary>
    private static async Task<string> ReadUtf8Async(HttpContent content, CancellationToken ct)
    {
        var bytes = await content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Reads the body as UTF-8 text up to <paramref name="maxBytes"/>; returns null when the body is longer (the caller fails open).
    /// Reading stops at maxBytes+1 — a huge robots.txt never balloons memory.</summary>
    private static async Task<string?> ReadCappedAsync(HttpContent content, int maxBytes, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        var buffer = new byte[maxBytes + 1];
        var total = 0;

        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }

        return total > maxBytes ? null : Encoding.UTF8.GetString(buffer, 0, total);
    }

    /// <summary>The robots origin key: scheme + host + explicit port (only when non-default). Case-insensitive — origins are few.</summary>
    private static string OriginKey(Uri uri) => $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? string.Empty : $":{uri.Port}")}";

    private static string DecisionName(bool allowed) => allowed ? "allowed" : "blocked";
}
