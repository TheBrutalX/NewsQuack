using System.Text;           // Encoding.UTF8 — the JSON content encoding
using System.Text.Json;      // JsonSerializer — the notification payload (BCL)
using NewsQuack.App.Http;     // NamedHttpClientRegistration.DiscordClientName — the named "discord" client (frozen name, T-005)
using Microsoft.Extensions.Logging;   // ILogger<T>

namespace NewsQuack.App.Sending;

/// <summary>T-022: one dead-letter event as delivered to the system webhook — exactly the fields an operator needs (decision 5). FailedAtUtc is ISO-8601 UTC, pre-formatted by the sink.</summary>
public sealed record DeadLetterNotification(
    string InstanceId,
    string FeedUrl,
    string ItemId,
    string Title,
    string ErrorCategory,
    string? LastError,
    string FailedAtUtc);

/// <summary>
/// T-022: the system-webhook notifier for dead-letter events — one JSON POST per dead letter so operators see repeated failures without reading logs. The webhook URL comes from the operational env var
/// NEWSQUACK_DEADLETTER_WEBHOOK (same pattern as NEWSQUACK_DATABASE — not a config schema key): missing/empty ⇒ no-op; present but not an absolute http(s) URL ⇒ disabled with a Warning at construction.
/// Uses the named "discord" client (plain JSON POST with the configured timeout — no Discord-specific behavior in the client itself). Notification failures are caught (exception as argument), logged
/// Warning, and never thrown — a broken ops webhook must not affect delivery.
/// </summary>
public sealed class DeadLetterNotifier
{
    /// <summary>The operational env var holding the dead-letter webhook URL (not a config schema key — same pattern as NEWSQUACK_DATABASE).</summary>
    public const string WebhookEnvironmentVariable = "NEWSQUACK_DEADLETTER_WEBHOOK";

    private readonly HttpClient? _http;       // null ⇒ disabled (missing/empty or invalid URL) — the ctor sets _http and _webhookUrl together, never one without the other
    private readonly string? _webhookUrl;
    private readonly ILogger<DeadLetterNotifier> _logger;

    public DeadLetterNotifier(string? webhookUrl, IHttpClientFactory httpFactory, ILogger<DeadLetterNotifier> logger)
    {
        ArgumentNullException.ThrowIfNull(httpFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;

        if (string.IsNullOrWhiteSpace(webhookUrl))
            return;   // missing/empty ⇒ no-op (decision 5) — the notifier is constructed but never posts

        if (!Uri.TryCreate(webhookUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            logger.LogWarning("Dead-letter webhook URL \"{WebhookUrl}\" is not an absolute http(s) URL — the notifier is disabled", webhookUrl);
            return;   // present but invalid ⇒ disabled with a Warning at construction (decision 5)
        }

        _webhookUrl = webhookUrl;
        _http = httpFactory.CreateClient(NamedHttpClientRegistration.DiscordClientName);   // the named "discord" client — plain JSON POST, no Discord-specific behavior in the client itself
    }

    /// <summary>True when a valid absolute http(s) URL was configured at construction.</summary>
    public bool IsEnabled => _http is not null;

    /// <summary>Fires one JSON POST for a dead-letter event. Never throws except OperationCanceledException on caller cancellation (the worker's shutdown owns it): every other failure is caught and logged Warning.</summary>
    public async Task NotifyAsync(DeadLetterNotification notification, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(notification);

        if (_http is not { } http || _webhookUrl is not { } url)   // disabled at construction — no-op (ctor invariant: both fields are set together)
            return;

        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(notification), Encoding.UTF8, "application/json");
            using var response = await http.PostAsync(url, content, ct);

            if (!response.IsSuccessStatusCode)
                _logger.LogWarning("Dead-letter notification to {WebhookUrl} returned HTTP {StatusCode}", url, (int)response.StatusCode);   // a non-success answer is a notification failure — logged, never thrown
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // shutdown owns cancellation — propagates to the worker's ExecuteAsync (same pattern as DiscordNotificationTarget)
        }
        catch (Exception ex)   // a broken ops webhook must not affect delivery (decision 5) — exception as argument, stack trace survives (AGENTS.md)
        {
            _logger.LogWarning(ex, "Dead-letter notification to {WebhookUrl} failed: {Message}", url, ex.Message);
        }
    }
}
