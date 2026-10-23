using System.Diagnostics;   // Stopwatch for per-stage and per-item durations (not a date — no UTC rule involved)
using NewsQuack.Core;
using Microsoft.Extensions.Logging;   // ILogger<T>

namespace NewsQuack.App.Pipeline;

/// <summary>
/// T-024: the ordered pipeline runner (coordinator decision 2). Executes every <see cref="IPostStage"/> against one item's
/// <see cref="PostContext"/>, in ascending <see cref="IPostStage.Order"/> order, applying each stage's declared
/// <see cref="FailurePolicy"/> on failure and emitting structured transit logs. Stateless singleton: the stages are resolved as
/// <c>IEnumerable&lt;IPostStage&gt;</c> (never concrete types) and sorted ONCE in the constructor — LINQ <c>OrderBy</c> is stable, so
/// registration order breaks ties. One instance serves all instances/feeds; thread-safety comes from the stages themselves.
/// </summary>
public sealed class PostPipelineRunner
{
    private readonly IReadOnlyList<IPostStage> _stages;   // sorted by Order (stable) — fixed for the runner's lifetime
    private readonly ILogger<PostPipelineRunner> _logger;

    public PostPipelineRunner(IEnumerable<IPostStage> stages, ILogger<PostPipelineRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(stages);
        ArgumentNullException.ThrowIfNull(logger);

        // Stable sort by Order once at construction (decision 2): ties keep registration order. The runner never re-sorts per item.
        _stages = stages.OrderBy(stage => stage.Order).ToList();
        _logger = logger;
    }

    /// <summary>
    /// Runs the ordered stage chain for one item. Cancellation is NOT a failure: an <see cref="OperationCanceledException"/> propagates out
    /// untouched (the shutdown path owns it). Any other exception from a stage applies that stage's policy — IsolateItem drops the item
    /// (Skip + Error log), ContinueDegraded logs a Warning and continues with the post as-is. After the chain, one Information summary line
    /// per item records the final outcome, stages-run count and total duration.
    /// </summary>
    public async Task RunAsync(PostContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var totalStopwatch = Stopwatch.StartNew();
        var executedCount = 0;   // stages actually run (a stage skipped because an earlier one set Skip is not counted)
        var isolated = false;    // true when a failing IsolateItem stage dropped the item (distinguishes "isolated" from a normal "skipped")

        foreach (var stage in _stages)
        {
            if (ctx.Skip)   // check after every stage — stop processing this item as soon as any stage set Skip
                break;

            var stageStopwatch = Stopwatch.StartNew();

            try
            {
                await stage.ExecuteAsync(ctx, ct);
            }
            catch (OperationCanceledException)
            {
                throw;   // cancellation is not a failure — the shutdown path owns it; propagate out of RunAsync untouched (decision 2)
            }
            catch (Exception ex)   // any other stage fault — apply THIS stage's declared policy, never swallow silently
            {
                if (stage.FailurePolicy == FailurePolicy.IsolateItem)
                {
                    ctx.Skip = true;   // drop the item — no further stages run on it
                    isolated = true;

                    _logger.LogError(ex, "Pipeline stage '{StageName}' failed — isolating the item: instance {InstanceId}, item {ItemId}",
                        stage.Name, ctx.InstanceId, ctx.CurrentPost.ItemId);
                }
                else   // ContinueDegraded: optional enrichment only — log and continue with the post as-is (never drop it)
                {
                    _logger.LogWarning(ex, "Pipeline stage '{StageName}' failed — continuing degraded: instance {InstanceId}, item {ItemId}",
                        stage.Name, ctx.InstanceId, ctx.CurrentPost.ItemId);
                }
            }

            executedCount++;   // this stage was executed (its fault, if any, was handled by its policy above)

            // Structured transit log per executed stage: name + duration + outcome ("skip" only when THIS stage set Skip).
            _logger.LogDebug("Pipeline stage executed: instance {InstanceId}, item {ItemId}, stage {StageName}, durationMs {DurationMs}, outcome {Outcome}",
                ctx.InstanceId, ctx.CurrentPost.ItemId, stage.Name, (int)stageStopwatch.ElapsedMilliseconds, ctx.Skip ? "skip" : "ok");
        }

        totalStopwatch.Stop();

        // Final outcome: completed = reached the end without Skip; isolated = a failing IsolateItem stage dropped it; skipped = a stage set Skip normally.
        var finalOutcome = !ctx.Skip ? "completed" : (isolated ? "isolated" : "skipped");

        _logger.LogInformation("Pipeline run finished: instance {InstanceId}, item {ItemId}, outcome {Outcome}, stagesRun {StagesRun}, totalMs {TotalMs}",
            ctx.InstanceId, ctx.CurrentPost.ItemId, finalOutcome, executedCount, (int)totalStopwatch.ElapsedMilliseconds);
    }
}
