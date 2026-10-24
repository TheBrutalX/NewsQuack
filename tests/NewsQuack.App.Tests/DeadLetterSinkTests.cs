using System.Text.Json;   // JsonDocument — asserting key fields in the stored payload JSON
using NewsQuack.App.Http;  // NamedHttpClientRegistration.DiscordClientName — the named "discord" client (frozen name, T-005)
using NewsQuack.App.Sending;   // DeadLetterSink + the internal ErrorCategory classifier (InternalsVisibleTo)
using NewsQuack.Core;          // Post / TargetResult / TargetOutcome — the frozen contracts
using Microsoft.Extensions.DependencyInjection;   // ServiceCollection — a real IHttpClientFactory for the (disabled) notifier
using Microsoft.Extensions.Logging;   // LogLevel — log assertions
using Xunit;

namespace NewsQuack.App.Tests;

/// <summary>
/// T-022: dead-letter sink + error-category classifier. The classifier is an internal static — every branch of decision 3 pinned directly. The sink is exercised over the recording store fake (dead-letter
/// calls assertable) with a DISABLED notifier (null URL — no HTTP involved): persistence + the single structured Warning are the unit under test here; notifier behavior has its own file. The real
/// IHttpClientFactory comes from a minimal container registering exactly the named "discord" client production registers (T-005).
/// </summary>
public class DeadLetterSinkTests : IDisposable
{
    private const string InstanceId = "inst-1";
    private const string FeedUrl = "https://example.com/feed.xml";

    private readonly ServiceProvider _provider;   // holds the IHttpClientFactory — disposed with the test (one instance per test)

    public DeadLetterSinkTests()
    {
        var services = new ServiceCollection();
        services.AddHttpClient(NamedHttpClientRegistration.DiscordClientName);   // exactly what production registers (T-005) — the disabled notifier never creates a client from it
        _provider = services.BuildServiceProvider();
    }

    public void Dispose() => _provider.Dispose();

    private static Post MakePost(string itemId, string title, string? link) => new()
    {
        Id = Guid.NewGuid().ToString(),
        ItemId = itemId,
        Title = title,
        Link = link,
        PublishedAt = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero),   // always UTC
        SourceUrl = FeedUrl,
    };

    private DeadLetterSink CreateSink(RecordingStateStore store, LogCapture capture) =>
        new(InstanceId, store,
            new DeadLetterNotifier(null, _provider.GetRequiredService<IHttpClientFactory>(), capture.For<DeadLetterNotifier>()),   // null URL ⇒ disabled notifier — no HTTP in these tests
            capture.For<DeadLetterSink>());

    // ── Classifier: every branch of decision 3 pinned directly ────────────────────────────────────────────────

    [Fact]
    public void Classify_PermanentFailure_Returns400Payload()
        => Assert.Equal("400-payload", DeadLetterSink.ErrorCategory.Classify(TargetResult.PermanentFailure("403: forbidden")));   // v1.0's only permanent class

    [Fact]
    public void Classify_RetryAfterPresent_Returns429()
        => Assert.Equal("429", DeadLetterSink.ErrorCategory.Classify(TargetResult.Retryable(TimeSpan.FromSeconds(5), "Discord rate limit")));   // structural — only rate limits carry a hint

    [Fact]
    public void Classify_TimeoutOrTransportError_ReturnsTimeout()
    {
        Assert.Equal("timeout", DeadLetterSink.ErrorCategory.Classify(TargetResult.Retryable(null, "timeout")));                                   // the T-020 client-timeout shape...
        Assert.Equal("timeout", DeadLetterSink.ErrorCategory.Classify(TargetResult.Retryable(null, "transport failure: Connection refused (https://example.com)")));   // ...and the transport-failure shape — network-unreachable groups into timeout for alerting
    }

    [Fact]
    public void Classify_Http5xxString_Returns5xx()
    {
        Assert.Equal("5xx", DeadLetterSink.ErrorCategory.Classify(TargetResult.Retryable(null, "HTTP 503")));   // the T-020 5xx error shape...
        Assert.Equal("5xx", DeadLetterSink.ErrorCategory.Classify(TargetResult.Retryable(null, "HTTP 599")));   // ...any HTTP 5\d\d
    }

    [Fact]
    public void Classify_UnknownRetryable_FallsBackTo5xx()
    {
        Assert.Equal("5xx", DeadLetterSink.ErrorCategory.Classify(TargetResult.Retryable(null, "something entirely different")));   // documented fallback — the generic server-side transient bucket...
        Assert.Equal("5xx", DeadLetterSink.ErrorCategory.Classify(TargetResult.Retryable(null, null)));                                  // ...even with no error string at all
    }

    // ── Sink: persistence + the single structured Warning ────────────────────────────────────────────────────

    [Fact]
    public async Task Sink_Record_PersistsFullPayloadAndLogsWarningWithAllFields()
    {
        var capture = new LogCapture();
        var store = new RecordingStateStore();
        var sink = CreateSink(store, capture);

        var post = MakePost("item-7", "Broken post", "https://example.com/item-7");
        await sink.RecordAsync(post, 5, TargetResult.Retryable(null, "HTTP 503"));   // must complete without throwing (decision 4)

        // The dead letter is persisted with every field an operator needs...
        Assert.Single(store.RecordDeadLetterCalls);
        var data = store.RecordDeadLetterCalls[0];   // ! safe: Single above guarantees the entry
        Assert.Equal(InstanceId, data.InstanceId);   // the sink's own instance id (the Post carries none)...
        Assert.Equal(FeedUrl, data.FeedUrl);         // ...post.SourceUrl as the feed url...
        Assert.Equal("item-7", data.ItemId);         // ...the post's stable dedup key...
        Assert.Equal(5, data.Attempts);              // ...attempts made...
        Assert.Equal("5xx", data.ErrorCategory);     // ...category classified from the last TargetResult (decision 3)...
        Assert.Equal("HTTP 503", data.LastError);    // ...and the last error as reported by the target
        Assert.True(data.FailedAtUtc.Offset == TimeSpan.Zero);   // UTC only (AGENTS.md)

        // ...with the FULL post as JSON — key fields present in the stored payload...
        using var document = JsonDocument.Parse(data.PayloadJson);
        var root = document.RootElement;
        Assert.Equal("item-7", root.GetProperty("ItemId").GetString());
        Assert.Equal("Broken post", root.GetProperty("Title").GetString());
        Assert.Equal("https://example.com/item-7", root.GetProperty("Link").GetString());

        // ...and ONE structured Warning carries every field (no exception argument on the happy path).
        var warning = capture.Find(LogLevel.Warning, "Dead letter", "item-7");
        Assert.NotNull(warning);
        Assert.Null(warning!.Exception);   // ! safe: NotNull above guarantees the entry
        Assert.Contains(InstanceId, warning.Message);
        Assert.Contains(FeedUrl, warning.Message);
        Assert.Contains("Broken post", warning.Message);
        Assert.Contains("attempts made 5", warning.Message);
        Assert.Contains("5xx", warning.Message);
        Assert.Contains("HTTP 503", warning.Message);

        // And no Error was logged — the happy path is a single Warning (T-021's dead-letter line moved here, issue #23).
        Assert.Equal(0, capture.CountAt(LogLevel.Error));
    }

    [Fact]
    public async Task Sink_StoreThrows_LogsErrorAndDoesNotThrow()
    {
        var capture = new LogCapture();
        var store = new RecordingStateStore { RecordDeadLetterException = new InvalidOperationException("sqlite busy") };   // simulate a store outage at dead-letter time
        var sink = CreateSink(store, capture);

        await sink.RecordAsync(MakePost("item-1", "Doomed post", null), 3, TargetResult.Retryable(null, "HTTP 500"));   // must NOT throw (decision 4)

        // The store failure is logged Error with the exception as argument — stack trace survives (AGENTS.md)...
        var error = capture.Find(LogLevel.Error, "Dead-letter persistence failed", "item-1");
        Assert.NotNull(error);
        Assert.IsType<InvalidOperationException>(error!.Exception);   // ! safe: NotNull above guarantees the entry

        // ...and the dead letter was still logged (the Warning is not suppressed by a store failure) — at-least-once covers persistence.
        Assert.NotNull(capture.Find(LogLevel.Warning, "Dead letter", "item-1"));
    }
}
