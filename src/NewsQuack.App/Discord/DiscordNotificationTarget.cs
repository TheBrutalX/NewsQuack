using System.Net;
using System.Text;
using System.Text.Json;
using NewsQuack.App.Configuration;
using NewsQuack.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;   // NullLogger<T> fallback when no logger is supplied (tests / direct construction)

namespace NewsQuack.App.Discord;

/// <summary>
/// The v1.0 notification target (T-020) — everything Discord-specific lives here and nowhere else: payload construction for the
/// three modes, hard limits, guaranteed fallback image, a one-shot forum/text swap on channel-type 400 errors, and HTTP response →
/// TargetResult mapping. At most two POSTs per SendAsync call (initial + one swap); retry/backoff/dead-letter belong to the send
/// worker (a later task), never this class.
/// </summary>
public sealed class DiscordNotificationTarget : INotificationTarget
{
    /// <summary>Name of the IHttpClientFactory client all webhook traffic goes through. A later task configures that named client's
    /// timeout/redirect behavior; until then the default client settings apply (this target never sets timeouts on the instance).</summary>
    public const string HttpClientName = "discord";

    private readonly HttpClient _http;
    private readonly DiscordTargetConfig _config;
    private readonly string _webhookUrl;
    private readonly ILogger<DiscordNotificationTarget> _logger;

    public DiscordNotificationTarget(IHttpClientFactory httpFactory, DiscordTargetConfig config, ILogger<DiscordNotificationTarget>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(httpFactory);

        if (config is null)
            throw new ArgumentNullException(nameof(config));

        if (string.IsNullOrWhiteSpace(config.WebhookUrl))
            throw new ArgumentException("DiscordTargetConfig.WebhookUrl must be set (config validation guarantees this at boot).", nameof(config));

        _http = httpFactory.CreateClient(HttpClientName);   // always the named "discord" client — never a raw HttpClient
        _config = config;
        _webhookUrl = config.WebhookUrl!;   // ! safe: IsNullOrWhiteSpace check above guarantees non-null, non-empty
        _logger = logger ?? NullLogger<DiscordNotificationTarget>.Instance;
    }

    public async Task<TargetResult> SendAsync(Post post, CancellationToken ct = default)
    {
        var mode = DiscordPayloadBuilder.ParseMode(_config.Mode);
        var first = await PostOnceAsync(post, mode, ct);

        if (IsChannelTypeMismatch(first))
        {
            // One-shot swap: forum → embed, everything else → forum. Never a third attempt — the second response maps normally below.
            var swapped = mode == DiscordMode.Forum ? DiscordMode.Embed : DiscordMode.Forum;
            _logger.LogWarning("Discord channel-type mismatch (400) at {WebhookUrl}; resending post {PostId} once as {SwappedMode}", WebhookMasker.Mask(_webhookUrl), post.Id, swapped);

            var second = await PostOnceAsync(post, swapped, ct);
            return MapAttempt(second, post);
        }

        return MapAttempt(first, post);
    }

    /// <summary>One POST of the payload for <paramref name="mode"/>. Transport failures (connection error, client timeout) are caught,
    /// logged with the exception as the logger argument (stack trace survives), and returned as a failure reason — never rethrown.</summary>
    private async Task<DiscordAttempt> PostOnceAsync(Post post, DiscordMode mode, CancellationToken ct)
    {
        var json = DiscordPayloadBuilder.BuildPayload(post, _config, mode);

        try
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(_webhookUrl, content, ct);

            return new DiscordAttempt(
                response.StatusCode,
                await response.Content.ReadAsStringAsync(ct),   // body kept for retry-after parsing / 400 inspection / error excerpt
                response.Headers.RetryAfter?.Delta,             // Retry-After header in seconds when present (date form → null → body)
                null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // the caller cancelled — propagate it, the worker decides what to do with a cancelled send
        }
        catch (OperationCanceledException ex)
        {
            // Client-level timeout surfaces as TaskCanceledException without our token being cancelled.
            _logger.LogWarning(ex, "Discord webhook request timed out: {WebhookUrl}", WebhookMasker.Mask(_webhookUrl));
            return new DiscordAttempt(null, string.Empty, null, "timeout");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Discord webhook request failed: {WebhookUrl}", WebhookMasker.Mask(_webhookUrl));
            return new DiscordAttempt(null, string.Empty, null, $"transport failure: {ex.Message}");
        }
    }

    /// <summary>Maps one attempt to a TargetResult. A transport failure/timeout is retryable without a hint; 204 → Delivered;
    /// 429 → Retryable with the best available retry-after; any 5xx → Retryable without a hint; everything else (any other 4xx,
    /// including a channel-type 400 on the second attempt) → PermanentFailure.</summary>
    private TargetResult MapAttempt(DiscordAttempt attempt, Post post)
    {
        if (attempt.FailureReason is { } reason)
            return TargetResult.Retryable(null, reason);

        var status = attempt.StatusCode!.Value;   // ! safe: a null FailureReason means a response was received (record invariant)

        switch (status)
        {
            case HttpStatusCode.NoContent:
                return TargetResult.Delivered();

            case HttpStatusCode.TooManyRequests:
                return TargetResult.Retryable(ParseRetryAfter(attempt), "Discord rate limit");

            default:
                break;
        }

        if ((int)status is >= 500 and < 600)
            return TargetResult.Retryable(null, $"HTTP {(int)status}");

        var error = FormatError(status, attempt.Body);
        _logger.LogWarning("Discord webhook rejected post {PostId} at {WebhookUrl}: {Error}", post.Id, WebhookMasker.Mask(_webhookUrl), error);
        return TargetResult.PermanentFailure(error);
    }

    /// <summary>Retry-after resolution order (T-020): the Retry-After header (seconds) when present, else the JSON body's
    /// retry_after_ms (milliseconds), else retry_after (seconds); null when neither is parseable — the worker then falls back to
    /// its own base backoff. Non-JSON bodies must not crash parsing.</summary>
    private TimeSpan? ParseRetryAfter(DiscordAttempt attempt)
    {
        if (attempt.RetryAfterHeader is { } headerDelta)
            return headerDelta;

        try
        {
            using var document = JsonDocument.Parse(attempt.Body);
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("retry_after_ms", out var ms) && ms.ValueKind == JsonValueKind.Number && ms.TryGetDouble(out var msValue))
                    return TimeSpan.FromMilliseconds(msValue);

                if (root.TryGetProperty("retry_after", out var seconds) && seconds.ValueKind == JsonValueKind.Number && seconds.TryGetDouble(out var sValue))
                    return TimeSpan.FromSeconds(sValue);
            }
        }
        catch (JsonException ex)   // non-JSON 429 body — no retry-after available, the worker's base backoff applies
        {
            _logger.LogDebug(ex, "Discord 429 body was not valid JSON; no retry-after parsed: {WebhookUrl}", WebhookMasker.Mask(_webhookUrl));
        }

        return null;
    }

    /// <summary>True when a 400 body signals Discord's channel-type mismatch — error code 50_071 anywhere in the JSON (string or
    /// numeric form) or "Channel type not supported" message text, case-insensitive. A non-JSON 400 body is not a signal here and is
    /// mapped as a plain 4xx below (its excerpt still lands in the PermanentFailure log line, so nothing is lost silently).</summary>
    private static bool IsChannelTypeMismatch(DiscordAttempt attempt)
    {
        if (attempt.StatusCode != HttpStatusCode.BadRequest || string.IsNullOrWhiteSpace(attempt.Body))
            return false;

        try
        {
            using var document = JsonDocument.Parse(attempt.Body);
            return ContainsChannelTypeSignal(document.RootElement);
        }
        catch (JsonException)   // non-JSON 400 body → not a channel-type signal, mapped as a plain 4xx by MapAttempt
        {
            return false;
        }
    }

    private static bool ContainsChannelTypeSignal(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())   // Discord nests the code under "errors" for some payload shapes — walk everything
                {
                    if (property.Name.Equals("code", StringComparison.OrdinalIgnoreCase))
                    {
                        if (property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() == "50_071")
                            return true;

                        if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var code) && code == 50_071)
                            return true;
                    }

                    if (property.Name.Equals("message", StringComparison.OrdinalIgnoreCase)
                        && property.Value.ValueKind == JsonValueKind.String
                        && property.Value.GetString()!.Contains("channel type not supported", StringComparison.OrdinalIgnoreCase))   // ! safe: ValueKind == String guarantees a string here
                        return true;

                    if (ContainsChannelTypeSignal(property.Value))
                        return true;
                }

                return false;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    if (ContainsChannelTypeSignal(item))
                        return true;

                return false;

            default:
                return false;
        }
    }

    /// <summary>"&lt;status&gt;: &lt;short body excerpt&gt;" — the body flattened to one line and capped so a verbose error page can't bloat dead-letter entries.</summary>
    private static string FormatError(HttpStatusCode status, string body)
    {
        var excerpt = string.Join(" ", body.Split('\n', '\r').Select(part => part.Trim()));

        if (excerpt.Length > 200)
            excerpt = excerpt[..199] + "…";

        return excerpt.Length == 0 ? $"{(int)status}" : $"{(int)status}: {excerpt}";
    }

    /// <summary>Outcome of one POST: either a full response (status + body + Retry-After header delta in seconds) or a transport-level
    /// failure reason. The two are mutually exclusive — exactly one of FailureReason / StatusCode is set.</summary>
    private sealed record DiscordAttempt(HttpStatusCode? StatusCode, string Body, TimeSpan? RetryAfterHeader, string? FailureReason);
}
