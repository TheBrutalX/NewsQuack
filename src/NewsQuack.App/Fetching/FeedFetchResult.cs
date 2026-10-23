namespace NewsQuack.App.Fetching;

/// <summary>The observable outcome of one feed fetch (T-010). A fetch never fails silently: every non-Ok status carries a short human-readable
/// reason, and the last HTTP status seen is preserved when there was one.</summary>
public enum FetchStatus
{
    /// <summary>The feed body was fetched (2xx) — <see cref="FeedFetchResult.Content"/> holds it as UTF-8 text.</summary>
    Ok,

    /// <summary>robots.txt disallows the feed's path — the feed itself was never requested.</summary>
    RobotsBlocked,

    /// <summary>A final non-2xx HTTP status after the User-Agent cascade was exhausted (or a non-cascading 4xx) — see <see cref="FeedFetchResult.StatusCode"/>.</summary>
    HttpError,

    /// <summary>The client's own per-client timeout fired (T-005) without caller cancellation.</summary>
    Timeout,

    /// <summary>A transport-level failure (connection refused/reset, DNS, …) — or a URL that is not an absolute http(s) URI at all.</summary>
    TransportError,
}

/// <summary>The result of one feed fetch — raw fetched content plus status; parsing belongs to a later task (T-012).</summary>
public sealed record FeedFetchResult(
    FetchStatus Status,
    string? Content = null,     // response body as UTF-8 text when Status == Ok
    int? StatusCode = null,     // last HTTP status seen (when any)
    string? Error = null);      // short human-readable reason for non-Ok statuses

/// <summary>The feed-fetching contract later tasks (schedulers/pipeline) resolve — never the concrete type.</summary>
public interface IFeedFetcher
{
    /// <summary>Fetches one feed URL through the named "feed" client under the global throttle, applying robots.txt rules per origin and the
    /// validated fallback-UA cascade. Caller cancellation propagates as <see cref="OperationCanceledException"/> — it is never mapped to a status.</summary>
    Task<FeedFetchResult> FetchAsync(string feedUrl, CancellationToken ct = default);
}
