using Xunit;

namespace NewsQuack.Core.Tests;

public class TargetResultTests
{
    [Fact]
    public void TargetResult_FactoryMethods_ProduceExpectedOutcomes()
    {
        var delivered = TargetResult.Delivered();
        Assert.Equal(TargetOutcome.Delivered, delivered.Outcome);
        Assert.Null(delivered.RetryAfter);
        Assert.Null(delivered.Error);

        var retryable = TargetResult.Retryable(TimeSpan.FromSeconds(30), "rate limited");
        Assert.Equal(TargetOutcome.Retryable, retryable.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(30), retryable.RetryAfter);
        Assert.Equal("rate limited", retryable.Error);

        var permanent = TargetResult.PermanentFailure("bad payload");
        Assert.Equal(TargetOutcome.PermanentFailure, permanent.Outcome);
        Assert.Null(permanent.RetryAfter);
        Assert.Equal("bad payload", permanent.Error);
    }
}
