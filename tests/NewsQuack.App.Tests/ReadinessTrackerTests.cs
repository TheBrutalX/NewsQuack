using NewsQuack.App.Health;
using Xunit;

namespace NewsQuack.App.Tests;

/// <summary>T-030 unit tests for the frozen ReadinessTracker API: vacuous readiness, missing-set reporting, idempotency and thread-safety.</summary>
public class ReadinessTrackerTests
{
    [Fact]
    public void IsReady_NoRequirementsRegistered_IsVacuouslyTrue()
    {
        var tracker = new ReadinessTracker();

        Assert.True(tracker.IsReady);   // nothing registered → ready by definition (T-030 registers no requirements)
        Assert.Empty(tracker.Missing);
    }

    [Fact]
    public void ReadinessTracker_MixedStates_ReportsMissingCorrectly()
    {
        var tracker = new ReadinessTracker();
        tracker.RegisterRequirement("a");
        tracker.RegisterRequirement("b");
        tracker.RegisterRequirement("c");

        // nothing ready yet → all three missing, in registration order (deterministic 503 body)
        Assert.False(tracker.IsReady);
        Assert.Equal(new[] { "a", "b", "c" }, tracker.Missing);

        tracker.MarkReady("b");
        Assert.False(tracker.IsReady);
        Assert.Equal(new[] { "a", "c" }, tracker.Missing);

        // MarkReady is idempotent — repeating it changes nothing
        tracker.MarkReady("b");
        Assert.False(tracker.IsReady);
        Assert.Equal(new[] { "a", "c" }, tracker.Missing);

        tracker.MarkReady("a");
        tracker.MarkReady("c");
        Assert.True(tracker.IsReady);   // every registered requirement ready → /readyz passes
        Assert.Empty(tracker.Missing);
    }

    [Fact]
    public void RegisterRequirement_SameNameTwice_MissingListsItOnce()
    {
        var tracker = new ReadinessTracker();
        tracker.RegisterRequirement("store");
        tracker.RegisterRequirement("store");   // wiring-time duplicates must not duplicate the missing set

        Assert.False(tracker.IsReady);
        Assert.Equal(new[] { "store" }, tracker.Missing);

        tracker.MarkReady("store");
        Assert.True(tracker.IsReady);
    }

    [Fact]
    public async Task MarkReady_ConcurrentCallsFrom50Tasks_AllRequirementsBecomeReady()
    {
        var tracker = new ReadinessTracker();
        for (var i = 0; i < 10; i++)
            tracker.RegisterRequirement($"r{i}");

        // 50 tasks each mark every requirement — concurrent MarkReady must be safe and lose no updates.
        await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 10; i++)
                tracker.MarkReady($"r{i}");
        })));

        Assert.True(tracker.IsReady);   // no lost updates: every requirement is ready
        Assert.Empty(tracker.Missing);
    }
}
