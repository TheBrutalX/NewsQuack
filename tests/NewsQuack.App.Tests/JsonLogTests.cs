using System.Globalization;
using System.Reflection;
using System.Text.Json;
using NewsQuack.App.Configuration;
using NewsQuack.App.Logging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace NewsQuack.App.Tests;

/// <summary>T-004 unit tests: the frozen JSON line format, UTC timestamps, exceptions, per-worker enrichment and concurrency.</summary>
public class JsonLogTests
{
    private const string Category = "NewsQuack.App.Program";

    /// <summary>Captures everything a single JsonLoggerProvider writes, through a real LoggerFactory (standard level filtering).</summary>
    private sealed class LogCapture : IDisposable
    {
        public StringWriter Output { get; } = new();

        private readonly ILoggerFactory _factory;

        public LogCapture(LogLevel minimumLevel = LogLevel.Trace)
        {
            var provider = new JsonLoggerProvider(Output);
            _factory = LoggerFactory.Create(builder => builder.AddProvider(provider).SetMinimumLevel(minimumLevel));
        }

        public ILogger Logger(string category = Category) => _factory.CreateLogger(category);

        /// <summary>All captured lines (one JSON object per line, trailing newline removed).</summary>
        public string[] Lines() => Output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        /// <summary>Parses one captured line as a self-contained JsonElement.</summary>
        public JsonElement ParseLine(int index = 0) => JsonSerializer.Deserialize<JsonElement>(Lines()[index]);

        public void Dispose() => _factory.Dispose();
    }

    [Fact]
    public void JsonLog_EmitInformationWithProps_ProducesOneValidJsonLineWithAllFields()
    {
        using var capture = new LogCapture();

        capture.Logger().LogInformation("Post pipeline finished for feed {SourceUrl}", ConfigTestHelper.FeedUrl);

        Assert.Single(capture.Lines());   // exactly one line — nothing else on the sink

        var doc = capture.ParseLine();
        Assert.Equal(JsonValueKind.Object, doc.ValueKind);

        var timestamp = doc.GetProperty("@timestamp").GetString()!;
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$", timestamp);   // ISO 8601 UTC, millisecond precision, Z suffix

        Assert.Equal("Information", doc.GetProperty("level").GetString());
        Assert.Equal(Category, doc.GetProperty("logger").GetString());
        Assert.Equal($"Post pipeline finished for feed {ConfigTestHelper.FeedUrl}", doc.GetProperty("message").GetString());   // rendered template
        var propNames = doc.GetProperty("props").EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Contains("SourceUrl", propNames);   // caller state present…
        Assert.Equal(ConfigTestHelper.FeedUrl, doc.GetProperty("props").GetProperty("SourceUrl").GetString());
        Assert.DoesNotContain("{OriginalFormat}", propNames);   // …framework-internal template copy is not
        Assert.Equal(JsonValueKind.Null, doc.GetProperty("exception").ValueKind);
    }

    [Fact]
    public void JsonLog_Timestamp_IsIso8601UtcZRegardlessOfHostCulture()
    {
        var original = CultureInfo.DefaultThreadCurrentCulture;

        try
        {
            // de-DE would mangle any CurrentCulture-dependent formatting (e.g. "23.10.2026 14:05:09,123") — the frozen pattern must not care.
            CultureInfo.DefaultThreadCurrentCulture = new CultureInfo("de-DE");

            // Deterministic half: a fixed instant renders exactly as the frozen example does, under the hostile culture.
            var line = JsonLogFormatter.Format(new DateTime(2026, 10, 23, 14, 5, 9, 123, DateTimeKind.Utc), LogLevel.Information, Category, "fixed", null, null);   // no state, no exception
            Assert.Contains("\"@timestamp\":\"2026-10-23T14:05:09.123Z\"", line);

            // End-to-end half: an emitted entry still ends in Z and parses back to the same UTC instant.
            using var capture = new LogCapture();
            var before = DateTime.UtcNow;
            capture.Logger().LogWarning("tick");
            var after = DateTime.UtcNow;

            var timestamp = capture.ParseLine().GetProperty("@timestamp").GetString()!;
            Assert.EndsWith("Z", timestamp);

            var parsed = DateTimeOffset.Parse(timestamp, CultureInfo.InvariantCulture);
            Assert.Equal(TimeSpan.Zero, parsed.Offset);   // UTC, not local time with a Z glued on
            // The emission instant itself — ±5 ms tolerance because millisecond formatting truncates.
            Assert.InRange(parsed.UtcDateTime, before.AddMilliseconds(-5), after.AddMilliseconds(5));
        }
        finally
        {
            CultureInfo.DefaultThreadCurrentCulture = original;
        }
    }

    [Fact]
    public void JsonLog_WithException_SerializesTypeMessageAndFullStackTrace()
    {
        using var capture = new LogCapture();

        var exception = Catch(Throw);   // a real throw through a delegate boundary — the stack trace has multiple frames separated by newlines
        Assert.NotNull(exception.StackTrace);
        Assert.Contains('\n', exception.StackTrace!);   // precondition: multi-frame, so the escaping assertion below is not vacuous

        capture.Logger().LogError(exception, "feed fetch failed");

        var rawLine = Assert.Single(capture.Lines());   // one line even with a multi-frame trace — newlines escaped, never raw
        Assert.Contains("\\n", rawLine);                // the two-character JSON escape is present in the serialized trace

        var doc = capture.ParseLine();
        var ex = doc.GetProperty("exception");
        Assert.Equal(JsonValueKind.Object, ex.ValueKind);
        Assert.Equal(typeof(InvalidOperationException).FullName, ex.GetProperty("type").GetString());
        Assert.Equal("boom", ex.GetProperty("message").GetString());
        Assert.Equal(exception.StackTrace, ex.GetProperty("stackTrace").GetString());   // exact equality — the full trace, untruncated
    }

    /// <summary>Throws a real exception (the delegate boundary keeps the JIT from inlining it into one frame).</summary>
    private static void Throw() => throw new InvalidOperationException("boom");

    /// <summary>Catches whatever <paramref name="action"/> throws and returns it.</summary>
    private static Exception Catch(Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            return e;
        }

        throw new InvalidOperationException("unreachable — the action was expected to throw");   // keeps the compiler honest about the contract above
    }

    [Fact]
    public void JsonLog_NoProps_OmitsPropsField()
    {
        using var capture = new LogCapture();

        capture.Logger().LogInformation("no structured state on this entry");

        var doc = capture.ParseLine();
        Assert.False(doc.TryGetProperty("props", out _));   // omitted entirely — not {} and not null
    }

    [Fact]
    public void JsonLog_PropValueNotJsonSerializable_FallsBackToStringRepresentation()
    {
        using var capture = new LogCapture();

        // ASP.NET Core routing diagnostics log endpoint metadata as a RuntimeMethodInfo (T-030 enabled WebApplication) — STJ cannot
        // serialize that by design. The provider must not throw, or Logger.ThrowLoggingError turns the logging error into a 500.
        var method = typeof(JsonLogTests).GetMethod(nameof(Throw), BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);   // precondition — the test helper above must exist

        capture.Logger().LogInformation("endpoint {Metadata}", method);

        var doc = capture.ParseLine();   // still exactly one valid JSON line
        var value = doc.GetProperty("props").GetProperty("Metadata");
        Assert.Equal(JsonValueKind.String, value.ValueKind);   // string fallback — the type's ToString() representation
    }

    [Fact]
    public void JsonLog_EnricherSet_InstanceAndUrlFieldsPresent()
    {
        using var capture = new LogCapture();

        var enricher = new LogEnricher();
        enricher.Set("main", ConfigTestHelper.FeedUrl);

        using (LogEnricher.Activate(enricher))
            capture.Logger().LogInformation("worker tick");

        capture.Logger().LogInformation("after the scope closed");   // same thread, outside the activated scope again

        var active = capture.ParseLine(0);
        Assert.Equal("main", active.GetProperty("instanceId").GetString());
        Assert.Equal(ConfigTestHelper.FeedUrl, active.GetProperty("url").GetString());

        var inactive = capture.ParseLine(1);
        Assert.False(inactive.TryGetProperty("instanceId", out _));   // scope ended → fields absent again (no leak)
        Assert.False(inactive.TryGetProperty("url", out _));
    }

    [Fact]
    public void JsonLog_EnricherNotSet_FieldsAbsent()
    {
        using var capture = new LogCapture();

        capture.Logger().LogInformation("no worker scope here");

        var doc = capture.ParseLine();
        Assert.False(doc.TryGetProperty("instanceId", out _));   // never emitted as null either — simply absent
        Assert.False(doc.TryGetProperty("url", out _));
    }

    [Fact]
    public async Task JsonLog_ConcurrentEmission_AllLinesValidJson()
    {
        using var capture = new LogCapture();

        const int tasks = 50;
        const int entriesPerTask = 20;

        await Task.WhenAll(Enumerable.Range(0, tasks).Select(async taskIndex =>
        {
            for (var i = 0; i < entriesPerTask; i++)
            {
                capture.Logger($"NewsQuack.App.Worker.{taskIndex}").LogInformation("entry {Index}", i);
                await Task.Yield();   // interleave tasks on the thread pool — exercises the provider's write gate
            }
        }));

        var lines = capture.Lines();
        Assert.Equal(tasks * entriesPerTask, lines.Length);   // nothing lost, nothing merged

        foreach (var line in lines)
            JsonSerializer.Deserialize<JsonElement>(line);     // every line is a complete JSON object — no interleaved half-lines
    }

    [Fact]
    public void JsonLog_WebhookUrlInMessage_EmittedAsIs_MaskingIsCallersResponsibility()
    {
        using var capture = new LogCapture();

        capture.Logger().LogInformation("posting to {WebhookUrl}", ConfigTestHelper.WebhookUrl);

        // The full URL — token included — must survive serialization: the formatter never masks, callers use WebhookMasker at echo sites.
        Assert.Contains(ConfigTestHelper.WebhookUrl, capture.Lines()[0]);
    }

    [Theory]
    [InlineData("Trace", LogLevel.Trace)]
    [InlineData("debug", LogLevel.Debug)]          // case-insensitive — the schema (EnumLike) accepts any casing
    [InlineData("Information", LogLevel.Information)]
    [InlineData("WARNING", LogLevel.Warning)]
    [InlineData("Error", LogLevel.Error)]
    [InlineData("Critical", LogLevel.Critical)]
    public void JsonLog_ParseLevel_SchemaValues_MapToLogLevel(string value, LogLevel expected) =>
        Assert.Equal(expected, JsonLoggerProvider.ParseLevel(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void JsonLog_ParseLevel_NotSet_UsesSchemaDefaultInformation(string? value) =>
        Assert.Equal(LogLevel.Information, JsonLoggerProvider.ParseLevel(value));

    [Fact]
    public void JsonLog_ParseLevel_UnknownValue_Throws() =>
        Assert.Throws<ArgumentException>(() => JsonLoggerProvider.ParseLevel("Verbose"));   // validation rejects it in config; direct callers still get a clear error
}
