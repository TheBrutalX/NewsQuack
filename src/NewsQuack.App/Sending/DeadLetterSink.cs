using System.Globalization;   // CultureInfo.InvariantCulture — the ISO-8601 UTC failed-at format (AGENTS.md: never CurrentCulture)
using System.Text.Json;       // JsonSerializer — the full post payload as JSON (decision 4: Core stays package-free, serialization lives here in App)
using NewsQuack.Core;          // Post / TargetResult / TargetOutcome / IStateStore / DeadLetterData — the frozen contracts
using Microsoft.Extensions.Logging;   // ILogger<T>

namespace NewsQuack.App.Sending;

/// <summary>
/// T-022: the dead-letter sink — one singleton per configured instance (keyed by instance id, T-021 registration pattern). When the send worker makes its
/// dead-letter decision (max attempts exhausted or permanent failure), it calls <see cref="RecordAsync"/> exactly once, which: serializes the FULL post to
/// JSON, persists a dead_letter_entries row via IStateStore (a store failure logs Error with the exception as argument and does NOT throw — at-least-once
/// covers it: the item was never confirmed, so the next cycle re-delivers and re-dead-letters if needed), emits ONE structured Warning carrying every field
/// an operator needs (T-021's dead-letter Error line moved here as a Warning per issue #23), and then awaits the system-webhook notifier. The sink never
/// throws out of RecordAsync except OperationCanceledException on caller cancellation — the worker's shutdown owns it (same pattern as SendWorker).
/// </summary>
public sealed class DeadLetterSink
{
    private readonly string _instanceId;   // this sink's instance — the Post carries no instance id, so each per-instance worker resolves ITS OWN sink
    private readonly IStateStore _store;
    private readonly DeadLetterNotifier _notifier;
    private readonly ILogger<DeadLetterSink> _logger;

    public DeadLetterSink(string instanceId, IStateStore store, DeadLetterNotifier notifier, ILogger<DeadLetterSink> logger)
    {
        ArgumentNullException.ThrowIfNull(instanceId);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(notifier);
        ArgumentNullException.ThrowIfNull(logger);

        _instanceId = instanceId;
        _store = store;
        _notifier = notifier;
        _logger = logger;
    }

    /// <summary>Persists + logs (one Warning) + notifies one dead letter. Never throws except OperationCanceledException on caller cancellation.</summary>
    public async Task RecordAsync(Post post, int attempts, TargetResult lastResult, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(post);
        ArgumentNullException.ThrowIfNull(lastResult);

        var category = ErrorCategory.Classify(lastResult);   // pure mapping (decision 3) — cannot throw
        var payloadJson = JsonSerializer.Serialize(post);     // the FULL post as JSON (decision 4) — operators see exactly what was being delivered
        var failedAtUtc = DateTimeOffset.UtcNow;             // one UTC instant for the persisted row AND the notification

        try
        {
            await _store.RecordDeadLetterAsync(new DeadLetterData(_instanceId, post.SourceUrl, post.ItemId, payloadJson, attempts, category, lastResult.Error, failedAtUtc), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // shutdown owns cancellation — the worker's ExecuteAsync handles it (same pattern as SendWorker/ConfirmDeliveryAsync)
        }
        catch (Exception ex)   // store failure: Error with the exception as argument, never rethrown — at-least-once covers it (decision 4)
        {
            _logger.LogError(ex, "Dead-letter persistence failed for instance {InstanceId}, item {ItemId}, feed {FeedUrl}: {Message}", _instanceId, post.ItemId, post.SourceUrl, ex.Message);
        }

        // ONE structured Warning — the dead-letter decision log (no exception argument on the happy path).
        _logger.LogWarning("Dead letter: instance {InstanceId}, item {ItemId}, feed {FeedUrl}, title \"{Title}\", attempts made {AttemptsMade}, category {ErrorCategory}, last error: {LastError}",
            _instanceId, post.ItemId, post.SourceUrl, post.Title, attempts, category, lastResult.Error ?? "unknown");

        await _notifier.NotifyAsync(new DeadLetterNotification(_instanceId, post.SourceUrl, post.ItemId, post.Title, category, lastResult.Error, failedAtUtc.ToString("O", CultureInfo.InvariantCulture)), ct);   // the notifier never throws (catches internally) — a broken ops webhook must not affect delivery
    }

    /// <summary>T-022 decision 3: maps the worker's last TargetResult to the entity's four error categories ("429" / "5xx" / "timeout" / "400-payload").
    /// Internal static so tests pin every branch directly.</summary>
    internal static class ErrorCategory
    {
        public const string Payload400 = "400-payload";   // v1.0's only permanent class (any non-2xx Discord 4xx)
        public const string RateLimited = "429";          // structural: only rate limits carry a RetryAfter hint
        public const string Timeout = "timeout";          // network-unreachable is grouped here for alerting; LastError keeps the exact reason
        public const string Server5xx = "5xx";            // generic server-side transient bucket — also the documented fallback

        public static string Classify(TargetResult result)
        {
            if (result.Outcome == TargetOutcome.PermanentFailure) return Payload400;   // v1.0's only permanent class (decision 3)
            if (result.RetryAfter is not null) return RateLimited;                     // structural — only rate limits carry a hint

            var error = result.Error ?? string.Empty;
            if (error.Contains("timeout", StringComparison.OrdinalIgnoreCase) || error.StartsWith("transport failure", StringComparison.Ordinal))
                return Timeout;   // network-unreachable groups into the timeout bucket for alerting; the raw LastError keeps the exact reason

            return Server5xx;   // both the ^HTTP 5\d\d$ shape (T-020's 5xx error string) and anything else land in "5xx" — the generic server-side transient bucket (documented fallback)
        }
    }
}
