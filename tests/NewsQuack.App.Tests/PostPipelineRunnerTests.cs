using NewsQuack.App.Pipeline;
using NewsQuack.Core;
using Microsoft.Extensions.Logging;   // LogLevel for log assertions
using Xunit;

namespace NewsQuack.App.Tests;

/// <summary>
/// T-024 unit: PostPipelineRunner against fake <see cref="IPostStage"/>s that record execution order and can set Skip or throw (with a chosen
/// policy), plus LogCapture for the transit-log assertions. Pins the binding runner semantics (coordinator decision 2): stable Order sort,
/// Skip check before every stage, OCE propagates untouched (never a failure), IsolateItem ⇒ Skip + Error log, ContinueDegraded ⇒ Warning + continue,
/// and the structured Debug-per-stage + Information-summary transit logs.
/// </summary>
public class PostPipelineRunnerTests
{
    private const string InstanceId = "inst-a";
    private const string ItemId = "item-1";

    /// <summary>A fake stage that records its execution (into a shared log), optionally throws, and optionally sets Skip. The policy is what the
    /// runner applies on failure — the stage just declares it.</summary>
    private sealed class FakeStage : IPostStage
    {
        private readonly List<string> _executionLog;

        public FakeStage(int order, string name, FailurePolicy policy, List<string> executionLog)
        {
            Order = order;
            Name = name;
            FailurePolicy = policy;
            _executionLog = executionLog;
        }

        public int Order { get; }
        public string Name { get; }
        public FailurePolicy FailurePolicy { get; }

        /// <summary>When set, ExecuteAsync throws this (before recording Skip) — simulates a stage fault.</summary>
        public Exception? Throw { get; set; }

        /// <summary>When true, ExecuteAsync sets ctx.Skip (simulates a normal skip decision, e.g. dedup).</summary>
        public bool SetSkip { get; set; }

        public Task ExecuteAsync(PostContext ctx, CancellationToken ct = default)
        {
            _executionLog.Add(Name);   // record that this stage actually ran (before any throw/skip side effect)

            if (Throw is { } ex) throw ex;
            if (SetSkip) ctx.Skip = true;

            return Task.CompletedTask;
        }
    }

    private static PostContext Context() => new()
    {
        InstanceId = InstanceId,
        CurrentPost = DedupTestPost.Create(ItemId),   // a minimal valid Post (UTC instant, stable item id)
    };

    // ── Ordering & Skip ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Run_StagesRegisteredOutOfOrder_ExecutedByAscendingOrder()
    {
        var log = new List<string>();
        var s30 = new FakeStage(30, "stage-30", FailurePolicy.IsolateItem, log);
        var s10 = new FakeStage(10, "stage-10", FailurePolicy.IsolateItem, log);
        var s20 = new FakeStage(20, "stage-20", FailurePolicy.IsolateItem, log);

        // Registered OUT of order — the runner must still execute by ascending Order (stable sort in the constructor).
        var runner = new PostPipelineRunner(new IPostStage[] { s30, s10, s20 }, new LogCapture().For<PostPipelineRunner>());

        await runner.RunAsync(Context());

        Assert.Equal(new[] { "stage-10", "stage-20", "stage-30" }, log);   // ascending Order regardless of registration order
    }

    [Fact]
    public async Task Run_EarlierStageSetsSkip_LaterStagesNotExecuted()
    {
        var log = new List<string>();
        var s10 = new FakeStage(10, "stage-10", FailurePolicy.IsolateItem, log) { SetSkip = true };   // e.g. dedup: already delivered
        var s20 = new FakeStage(20, "stage-20", FailurePolicy.IsolateItem, log);
        var s30 = new FakeStage(30, "stage-30", FailurePolicy.IsolateItem, log);

        var runner = new PostPipelineRunner(new IPostStage[] { s10, s20, s30 }, new LogCapture().For<PostPipelineRunner>());
        var ctx = Context();

        await runner.RunAsync(ctx);

        Assert.True(ctx.Skip);
        Assert.Equal(new[] { "stage-10" }, log);   // the Skip check before every stage stops the chain — later stages never run
    }

    // ── Failure policies ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Run_StageFailsIsolateItem_ItemSkippedAndChainStops()
    {
        var log = new List<string>();
        var capture = new LogCapture();
        var s10 = new FakeStage(10, "stage-10", FailurePolicy.IsolateItem, log) { Throw = new InvalidOperationException("boom") };
        var s20 = new FakeStage(20, "stage-20", FailurePolicy.IsolateItem, log);

        var runner = new PostPipelineRunner(new IPostStage[] { s10, s20 }, capture.For<PostPipelineRunner>());
        var ctx = Context();

        await runner.RunAsync(ctx);

        Assert.True(ctx.Skip);                       // IsolateItem ⇒ the item is dropped...
        Assert.Equal(new[] { "stage-10" }, log);     // ...and the chain stops (stage-20 never runs)
        var error = capture.Find(LogLevel.Error, "stage-10");   // Error log with the stage name (template + placeholders)...
        Assert.NotNull(error);                       // ...and the exception is attached (stack trace survives)
        Assert.Equal("boom", error!.Exception?.Message);
    }

    [Fact]
    public async Task Run_StageFailsContinueDegraded_ChainContinues()
    {
        var log = new List<string>();
        var capture = new LogCapture();
        var s10 = new FakeStage(10, "stage-10", FailurePolicy.ContinueDegraded, log) { Throw = new InvalidOperationException("degraded") };
        var s20 = new FakeStage(20, "stage-20", FailurePolicy.IsolateItem, log);

        var runner = new PostPipelineRunner(new IPostStage[] { s10, s20 }, capture.For<PostPipelineRunner>());
        var ctx = Context();

        await runner.RunAsync(ctx);

        Assert.False(ctx.Skip);                        // ContinueDegraded ⇒ the item is NOT dropped...
        Assert.Equal(new[] { "stage-10", "stage-20" }, log);   // ...the chain continues with the post as-is
        var warning = capture.Find(LogLevel.Warning, "stage-10");   // Warning log (not Error) for the degraded stage...
        Assert.NotNull(warning);                       // ...with the exception attached
        Assert.Equal("degraded", warning!.Exception?.Message);
    }

    [Fact]
    public async Task Run_AllStagesComplete_TransitLogsCarryStageDurationAndOutcome()
    {
        var log = new List<string>();
        var capture = new LogCapture();
        var s10 = new FakeStage(10, "stage-10", FailurePolicy.IsolateItem, log);
        var s20 = new FakeStage(20, "stage-20", FailurePolicy.IsolateItem, log);
        var s30 = new FakeStage(30, "stage-30", FailurePolicy.IsolateItem, log);

        var runner = new PostPipelineRunner(new IPostStage[] { s10, s20, s30 }, capture.For<PostPipelineRunner>());

        await runner.RunAsync(Context());

        // One Debug line per EXECUTED stage — each carries the stage name and outcome "ok" (none set Skip).
        Assert.Equal(3, capture.CountAt(LogLevel.Debug));
        Assert.NotNull(capture.Find(LogLevel.Debug, "stage-10", "ok"));
        Assert.NotNull(capture.Find(LogLevel.Debug, "stage-20", "ok"));
        Assert.NotNull(capture.Find(LogLevel.Debug, "stage-30", "ok"));

        // Exactly one Information summary per item: instance id + item id + final outcome + stages-run count.
        var summary = capture.Find(LogLevel.Information, InstanceId, ItemId, "completed", "stagesRun 3");
        Assert.NotNull(summary);   // completed (reached the end without Skip), all three stages ran
    }

    [Fact]
    public async Task Run_CancellationPropagates_NotTreatedAsFailure()
    {
        var log = new List<string>();
        var capture = new LogCapture();
        // A stage that throws OCE — the runner must propagate it untouched, NOT apply any failure policy.
        var s10 = new FakeStage(10, "stage-10", FailurePolicy.IsolateItem, log) { Throw = new OperationCanceledException() };
        var s20 = new FakeStage(20, "stage-20", FailurePolicy.IsolateItem, log);

        var runner = new PostPipelineRunner(new IPostStage[] { s10, s20 }, capture.For<PostPipelineRunner>());
        var ctx = Context();

        await Assert.ThrowsAsync<OperationCanceledException>(() => runner.RunAsync(ctx));   // OCE propagates out of RunAsync untouched

        Assert.False(ctx.Skip);                        // ...and was NOT treated as a failure (no isolation)...
        Assert.Null(capture.Find(LogLevel.Error, "stage-10"));   // ...no Error log...
        Assert.Null(capture.Find(LogLevel.Warning, "stage-10"));  // ...and no Warning log for the stage that threw OCE
    }
}
