using System.Globalization;
using System.Reflection;
using System.Text.Json;
using NewsQuack.App.Configuration;
using NewsQuack.App.Logging;
using Microsoft.Extensions.Logging;   // LogInformation/LogTrace/… extension methods on the bridge's M.E.L. loggers
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;

namespace NewsQuack.App.Tests;

/// <summary>T-054 unit tests (T-004's expectations, re-pointed): the frozen JSON line format through Serilog — UTC timestamps, exceptions, per-worker enrichment and concurrency.</summary>
public class JsonLogTests
{
    private const string Category = "NewsQuack.App.Program";

    /// <summary>The capture wiring every shape test uses — mirrors Program.cs's Serilog setup at Verbose (nothing suppressed) so the tests see every line.</summary>
    private static SerilogLineCapture Capture() => new(cfg =>
    {
        cfg.MinimumLevel.Is(LogEventLevel.Verbose);   // nothing suppressed — the frozen-shape assertions see every event
        cfg.Enrich.FromLogContext();                  // required: LogEnricher pushes "instanceId"/"url" through Serilog's LogContext (same as Program.cs)
    });

    [Fact]
    public void JsonLog_EmitInformationWithProps_ProducesOneValidJsonLineWithAllFields()
    {
        using var capture = Capture();

        capture.Logger(Category).LogInformation("Post pipeline finished for feed {SourceUrl}", ConfigTestHelper.FeedUrl);

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

            // Deterministic half: a fixed instant renders exactly as the frozen example does, under the hostile culture. The source-context
            // property is what the M.E.L. bridge sets to the category name — no props, no exception.
            var logEvent = new LogEvent(
                new DateTimeOffset(2026, 10, 23, 14, 5, 9, 123, TimeSpan.Zero),
                LogEventLevel.Information,
                null,
                new MessageTemplateParser().Parse("fixed"),
                [new LogEventProperty(Constants.SourceContextPropertyName, new ScalarValue(Category))]);

            using var writer = new StringWriter();
            new SerilogJsonLineFormatter().Format(logEvent, writer);
            Assert.Contains("\"@timestamp\":\"2026-10-23T14:05:09.123Z\"", writer.ToString());

            // End-to-end half: an emitted entry still ends in Z and parses back to the same UTC instant.
            using var capture = Capture();
            var before = DateTime.UtcNow;
            capture.Logger(Category).LogWarning("tick");
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
        using var capture = Capture();

        var exception = Catch(Throw);   // a real throw through a delegate boundary — the stack trace has multiple frames separated by newlines
        Assert.NotNull(exception.StackTrace);
        Assert.Contains('\n', exception.StackTrace!);   // precondition: multi-frame, so the escaping assertion below is not vacuous

        capture.Logger(Category).LogError(exception, "feed fetch failed");

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
        using var capture = Capture();

        capture.Logger(Category).LogInformation("no structured state on this entry");

        var doc = capture.ParseLine();
        Assert.False(doc.TryGetProperty("props", out _));   // omitted entirely — not {} and not null (pins that {OriginalFormat} never reaches props through the v10 bridge)
    }

    [Fact]
    public void JsonLog_PropValueNotJsonSerializable_FallsBackToStringRepresentation()
    {
        using var capture = Capture();

        // ASP.NET Core routing diagnostics log endpoint metadata as a RuntimeMethodInfo (T-030 enabled WebApplication) — STJ cannot
        // serialize that by design. The formatter must not throw, or Logger.ThrowLoggingError turns the logging error into a 500.
        var method = typeof(JsonLogTests).GetMethod(nameof(Throw), BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);   // precondition — the test helper above must exist

        capture.Logger(Category).LogInformation("endpoint {Metadata}", method!);

        var doc = capture.ParseLine();   // still exactly one valid JSON line
        var value = doc.GetProperty("props").GetProperty("Metadata");
        Assert.Equal(JsonValueKind.String, value.ValueKind);   // string fallback — the type's ToString() representation
    }

    [Fact]
    public void JsonLog_EnricherSet_InstanceAndUrlFieldsPresent()
    {
        using var capture = Capture();

        var enricher = new LogEnricher();
        enricher.Set("main", ConfigTestHelper.FeedUrl);

        using (LogEnricher.Activate(enricher))
            capture.Logger(Category).LogInformation("worker tick");

        capture.Logger(Category).LogInformation("after the scope closed");   // same thread, outside the activated scope again

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
        using var capture = Capture();

        capture.Logger(Category).LogInformation("no worker scope here");

        var doc = capture.ParseLine();
        Assert.False(doc.TryGetProperty("instanceId", out _));   // never emitted as null either — simply absent
        Assert.False(doc.TryGetProperty("url", out _));
    }

    [Fact]
    public async Task JsonLog_ConcurrentEmission_AllLinesValidJson()
    {
        using var capture = Capture();

        const int tasks = 50;
        const int entriesPerTask = 20;

        await Task.WhenAll(Enumerable.Range(0, tasks).Select(async taskIndex =>
        {
            for (var i = 0; i < entriesPerTask; i++)
            {
                capture.Logger($"NewsQuack.App.Worker.{taskIndex}").LogInformation("entry {Index}", i);
                await Task.Yield();   // interleave tasks on the thread pool — exercises the sink's per-event gate
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
        using var capture = Capture();

        capture.Logger(Category).LogInformation("posting to {WebhookUrl}", ConfigTestHelper.WebhookUrl);

        // The full URL — token included — must survive serialization: the formatter never masks, callers use WebhookMasker at echo sites.
        Assert.Contains(ConfigTestHelper.WebhookUrl, capture.Lines()[0]);
    }

    [Theory]
    [InlineData("Trace", LogEventLevel.Verbose)]
    [InlineData("debug", LogEventLevel.Debug)]          // case-insensitive — the schema (EnumLike) accepts any casing
    [InlineData("Information", LogEventLevel.Information)]
    [InlineData("WARNING", LogEventLevel.Warning)]
    [InlineData("Error", LogEventLevel.Error)]
    [InlineData("Critical", LogEventLevel.Fatal)]
    public void JsonLog_ParseLevel_SchemaValues_MapToSerilogLevel(string value, LogEventLevel expected) =>
        Assert.Equal(expected, SerilogLevels.ParseLevel(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void JsonLog_ParseLevel_NotSet_UsesSchemaDefaultInformation(string? value) =>
        Assert.Equal(LogEventLevel.Information, SerilogLevels.ParseLevel(value));

    [Fact]
    public void JsonLog_ParseLevel_UnknownValue_Throws() =>
        Assert.Throws<ArgumentException>(() => SerilogLevels.ParseLevel("Verbose"));   // validation rejects it in config; direct callers still get a clear error

    [Fact]
    public void JsonLog_EmitTraceAndCritical_LevelNamesRenderedAsTraceAndCriticalNotVerboseOrFatal()
    {
        using var capture = Capture();

        capture.Logger(Category).LogTrace("trace line");      // Serilog's "Verbose" must read as M.E.L.'s "Trace"…
        capture.Logger(Category).LogCritical("critical line");   // …and Serilog's "Fatal" as "Critical" (Boot_ValidConfigWithLogLevelError depends on this vocabulary)

        Assert.Equal("Trace", capture.ParseLine(0).GetProperty("level").GetString());
        Assert.Equal("Critical", capture.ParseLine(1).GetProperty("level").GetString());
    }

    [Fact]
    public void JsonLog_EnricherSetWithNullUrl_OnlyInstanceIdFieldPresent()
    {
        using var capture = Capture();

        var enricher = new LogEnricher();
        enricher.Set("main", null);   // only instanceId set — url must not be pushed at all (nulls are never pushed)

        using (LogEnricher.Activate(enricher))
            capture.Logger(Category).LogInformation("worker tick");

        var doc = capture.ParseLine();
        Assert.Equal("main", doc.GetProperty("instanceId").GetString());
        Assert.False(doc.TryGetProperty("url", out _));   // absent — never emitted as null either
    }

    [Fact]
    public void JsonLog_EnricherNestedScope_InnerValuesShadowOuter_AndPopRestoresOuter()
    {
        using var capture = Capture();

        // The scheduler's real pattern: an instance-only cycle scope with a nested per-feed scope carrying instance + url.
        var cycle = new LogEnricher();
        cycle.Set("main", null);

        using (LogEnricher.Activate(cycle))
        {
            capture.Logger(Category).LogInformation("cycle start");   // instanceId only

            var feed = new LogEnricher();
            feed.Set("main", ConfigTestHelper.FeedUrl);
            using (LogEnricher.Activate(feed))
                capture.Logger(Category).LogInformation("feed tick");   // both fields — the inner scope shadows the outer

            capture.Logger(Category).LogInformation("cycle end");   // back to instanceId only — LIFO pop restored the outer
        }

        var line0 = capture.ParseLine(0);
        Assert.Equal("main", line0.GetProperty("instanceId").GetString());
        Assert.False(line0.TryGetProperty("url", out _));

        var line1 = capture.ParseLine(1);
        Assert.Equal("main", line1.GetProperty("instanceId").GetString());
        Assert.Equal(ConfigTestHelper.FeedUrl, line1.GetProperty("url").GetString());

        var line2 = capture.ParseLine(2);
        Assert.Equal("main", line2.GetProperty("instanceId").GetString());
        Assert.False(line2.TryGetProperty("url", out _));   // no leak from the inner scope
    }
}
