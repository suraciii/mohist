using Mohist.Server.Workflow.Services;
using Xunit;
using Mohist.Server.Tests.Support;

namespace Mohist.Server.Tests.Specs.Workflow.Grain;

/// <summary>
/// Unit tests for <see cref="WorkflowSessionHealthGate"/>. The gate
/// classifies a session's context usage into one of three buckets
/// (Healthy / Warn / Block) and produces the user-facing blocking
/// message. The thresholds (80% warn, 90% block) are the
/// single source of truth for both the retry guard and the
/// dispatch-time health evaluation in <c>WorkflowGrain</c>.
/// </summary>
public class WorkflowSessionHealthGateSpecs
{
    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void NullUsage_IsHealthy()
    {
        var verdict = WorkflowSessionHealthGate.Evaluate(contextUsagePercent: null);
        Assert.Equal(HealthVerdict.Healthy, verdict);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Theory]
    [InlineData(0d)]
    [InlineData(45d)]
    [InlineData(60d)]
    [InlineData(79.99d)]
    [InlineData(79.999d)]
    public void BelowWarn_IsHealthy(double percent)
    {
        var verdict = WorkflowSessionHealthGate.Evaluate(percent);
        Assert.Equal(HealthVerdict.Healthy, verdict);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Theory]
    [InlineData(80d)]
    [InlineData(85d)]
    [InlineData(89.99d)]
    [InlineData(89.999d)]
    public void BetweenWarnAndBlock_IsWarn(double percent)
    {
        var verdict = WorkflowSessionHealthGate.Evaluate(percent);
        Assert.Equal(HealthVerdict.Warn, verdict);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Theory]
    [InlineData(90d)]
    [InlineData(92d)]
    [InlineData(95d)]
    [InlineData(100d)]
    public void AtOrAboveBlock_IsBlock(double percent)
    {
        var verdict = WorkflowSessionHealthGate.Evaluate(percent);
        Assert.Equal(HealthVerdict.Block, verdict);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void FromRawUsage_HonoursSameThresholds()
    {
        // 90_000 / 100_000 = 90% (exactly the block threshold).
        var verdict = WorkflowSessionHealthGate.Evaluate(contextWindowUsed: 90_000, contextWindowSize: 100_000);
        Assert.Equal(HealthVerdict.Block, verdict);

        // 70_000 / 100_000 = 70% (clearly under warn).
        var warnVerdict = WorkflowSessionHealthGate.Evaluate(contextWindowUsed: 70_000, contextWindowSize: 100_000);
        Assert.Equal(HealthVerdict.Healthy, warnVerdict);

        // 80_000 / 100_000 = 80% (warn).
        var warnVerdict2 = WorkflowSessionHealthGate.Evaluate(contextWindowUsed: 80_000, contextWindowSize: 100_000);
        Assert.Equal(HealthVerdict.Warn, warnVerdict2);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void FromRawUsage_MissingSize_IsHealthy()
    {
        var verdict = WorkflowSessionHealthGate.Evaluate(contextWindowUsed: 9_000, contextWindowSize: null);
        Assert.Equal(HealthVerdict.Healthy, verdict);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void BlockingMessage_WithPercent_IncludesPercentage()
    {
        var message = WorkflowSessionHealthGate.BuildBlockingMessage(92d);
        Assert.Contains("92", message);
        Assert.Contains("Compact", message);
        Assert.Contains("reset", message, StringComparison.OrdinalIgnoreCase);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void BlockingMessage_WithoutPercent_StillSuggestsRecovery()
    {
        var message = WorkflowSessionHealthGate.BuildBlockingMessage(contextUsagePercent: null);
        Assert.Contains("Compact", message);
        Assert.Contains("reset", message, StringComparison.OrdinalIgnoreCase);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void RecoveryActions_AdvertiseCompactAndReset()
    {
        Assert.Equal(new[] { WorkflowSessionHealthGate.RecoveryActionCompact, WorkflowSessionHealthGate.RecoveryActionReset },
            WorkflowSessionHealthGate.RecoveryActions);
    }
}
