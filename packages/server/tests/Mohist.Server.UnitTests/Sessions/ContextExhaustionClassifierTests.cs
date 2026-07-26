using System.Text.Json;
using Mohist.Server.Sessions.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Sessions;

/// <summary>
/// Unit tests for <see cref="ContextExhaustionClassifier"/> covering the
/// exhaustion classification rules in
/// <c>openspec/changes/issue-110/specs/context-exhaustion-detection/spec.md</c>.
/// </summary>
public class ContextExhaustionClassifierTests
{
    [Fact]
    public void ClassifyTurnFailure_FailedAbove90Percent_ClassifiesAsContextExhaustion()
    {
        var result = ContextExhaustionClassifier.ClassifyTurnFailure(
            status: "failed",
            contextWindowUsed: 960_000,
            contextWindowSize: 1_000_000,
            elapsed: TimeSpan.FromSeconds(120),
            producedExpectedOutput: false);

        Assert.Equal(ContextExhaustionClassifier.ContextExhaustionCategory, result.Category);
        Assert.True(result.IsExhausted);
        Assert.False(result.IsSuspected);
        Assert.Equal(96d, result.ContextUsagePercent);
    }

    [Fact]
    public void ClassifyTurnFailure_FailedAtExactly90Percent_ClassifiesAsContextExhaustion()
    {
        // The boundary is inclusive: a session that closed at exactly
        // 90% has no headroom and is treated as exhausted.
        var result = ContextExhaustionClassifier.ClassifyTurnFailure(
            status: "failed",
            contextWindowUsed: 90,
            contextWindowSize: 100,
            elapsed: TimeSpan.FromSeconds(60),
            producedExpectedOutput: true);

        Assert.Equal(ContextExhaustionClassifier.ContextExhaustionCategory, result.Category);
        Assert.True(result.IsExhausted);
    }

    [Fact]
    public void ClassifyTurnFailure_FailedBelow90Percent_DoesNotClassify()
    {
        var result = ContextExhaustionClassifier.ClassifyTurnFailure(
            status: "failed",
            contextWindowUsed: 70,
            contextWindowSize: 100,
            elapsed: TimeSpan.FromSeconds(60),
            producedExpectedOutput: false);

        Assert.Null(result.Category);
        Assert.False(result.IsExhausted);
    }

    [Fact]
    public void ClassifyTurnFailure_SuccessfulSessionAtHighUsage_DoesNotClassify()
    {
        // Successful close at high usage is healthy (auto-compact or
        // manual recovery brought it down before completion). The
        // classifier must not retroactively call that exhaustion.
        var result = ContextExhaustionClassifier.ClassifyTurnFailure(
            status: "completed",
            contextWindowUsed: 96,
            contextWindowSize: 100,
            elapsed: TimeSpan.FromMinutes(20),
            producedExpectedOutput: true);

        Assert.Null(result.Category);
        Assert.False(result.IsExhausted);
    }

    [Fact]
    public void ClassifyTurnFailure_CancelledSession_DoesNotClassify()
    {
        var result = ContextExhaustionClassifier.ClassifyTurnFailure(
            status: "cancelled",
            contextWindowUsed: 95,
            contextWindowSize: 100,
            elapsed: TimeSpan.FromMinutes(5),
            producedExpectedOutput: false);

        Assert.Null(result.Category);
        Assert.False(result.IsExhausted);
    }

    [Fact]
    public void ClassifyTurnFailure_FailedWithNoContextData_DoesNotClassify()
    {
        var result = ContextExhaustionClassifier.ClassifyTurnFailure(
            status: "failed",
            contextWindowUsed: null,
            contextWindowSize: null,
            elapsed: TimeSpan.FromSeconds(30),
            producedExpectedOutput: false);

        Assert.Null(result.Category);
        Assert.False(result.IsExhausted);
        Assert.Null(result.ContextUsagePercent);
    }

    [Fact]
    public void ClassifyRapidCompletion_FailedFastNoArtifactsAt85Percent_ClassifiesAsSuspected()
    {
        // Failed session that completed in < 10s without expected
        // output at >85% usage is also a rapid-completion pattern;
        // the heuristic must flag it as suspected.
        var result = ContextExhaustionClassifier.ClassifyRapidCompletion(
            status: "failed",
            contextUsagePercent: 88d,
            elapsed: TimeSpan.FromSeconds(5),
            producedExpectedOutput: false);

        Assert.Equal(ContextExhaustionClassifier.SuspectedContextExhaustionCategory, result.Category);
        Assert.True(result.IsSuspected);
        Assert.False(result.IsExhausted);
    }

    [Fact]
    public void ClassifyRapidCompletion_SuccessfulFastNoArtifactsAt85Percent_ClassifiesAsSuspected()
    {
        var result = ContextExhaustionClassifier.ClassifyRapidCompletion(
            status: "completed",
            contextUsagePercent: 88d,
            elapsed: TimeSpan.FromSeconds(5),
            producedExpectedOutput: false);

        Assert.Equal(ContextExhaustionClassifier.SuspectedContextExhaustionCategory, result.Category);
        Assert.True(result.IsSuspected);
    }

    [Fact]
    public void ClassifyRapidCompletion_ProducedExpectedOutput_DoesNotFlag()
    {
        var result = ContextExhaustionClassifier.ClassifyRapidCompletion(
            status: "completed",
            contextUsagePercent: 90d,
            elapsed: TimeSpan.FromSeconds(5),
            producedExpectedOutput: true);

        Assert.Null(result.Category);
        Assert.False(result.IsSuspected);
    }

    [Fact]
    public void ClassifyRapidCompletion_Below85Percent_DoesNotFlag()
    {
        // Below the 85% rapid-completion threshold — even a fast
        // session with no artifacts is not suspect.
        var result = ContextExhaustionClassifier.ClassifyRapidCompletion(
            status: "completed",
            contextUsagePercent: 30d,
            elapsed: TimeSpan.FromSeconds(3),
            producedExpectedOutput: false);

        Assert.Null(result.Category);
        Assert.False(result.IsSuspected);
    }

    [Fact]
    public void ClassifyRapidCompletion_LongerThan10Seconds_DoesNotFlag()
    {
        var result = ContextExhaustionClassifier.ClassifyRapidCompletion(
            status: "completed",
            contextUsagePercent: 95d,
            elapsed: TimeSpan.FromSeconds(45),
            producedExpectedOutput: false);

        Assert.Null(result.Category);
        Assert.False(result.IsSuspected);
    }

    [Fact]
    public void ClassifyRapidCompletion_NoUsageOrElapsed_DoesNotFlag()
    {
        var result = ContextExhaustionClassifier.ClassifyRapidCompletion(
            status: "completed",
            contextUsagePercent: null,
            elapsed: null,
            producedExpectedOutput: false);

        Assert.Null(result.Category);
    }

    [Fact]
    public void ApplyToPayload_AddsFailureCategoryAndExhaustionFlags()
    {
        const string input = "{\"status\":\"failed\",\"exitCode\":1,\"failureReason\":\"missing artifact\"}";

        var result = ContextExhaustionClassifier.ApplyToPayload(
            input,
            new ContextExhaustionClassifier.ClassificationResult(
                Category: "context_exhaustion",
                ContextUsagePercent: 96d,
                IsSuspected: false,
                IsExhausted: true));

        Assert.NotNull(result);
        using var doc = JsonDocument.Parse(result!);
        var root = doc.RootElement;
        Assert.Equal("context_exhaustion", root.GetProperty("failureCategory").GetString());
        Assert.Equal(96d, root.GetProperty("contextUsagePercent").GetDouble());
        Assert.True(root.GetProperty("contextExhaustion").GetBoolean());
        Assert.False(root.GetProperty("contextExhaustionSuspected").GetBoolean());
        Assert.Equal("failed", root.GetProperty("status").GetString());
        Assert.Equal(1, root.GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public void ApplyToPayload_PreservesExistingFailureCategoryOverrides()
    {
        // When the session.closed payload already carries a
        // failureCategory (e.g. from the runner), the classifier
        // rewrite should not silently drop unrelated fields.
        const string input = "{\"status\":\"failed\",\"failureCategory\":\"probe_timeout\"}";

        var result = ContextExhaustionClassifier.ApplyToPayload(
            input,
            new ContextExhaustionClassifier.ClassificationResult(
                Category: "context_exhaustion",
                ContextUsagePercent: 91d,
                IsSuspected: false,
                IsExhausted: true));

        Assert.NotNull(result);
        using var doc = JsonDocument.Parse(result!);
        var root = doc.RootElement;
        // Classifier wins (otherwise we would not have rewritten
        // the payload). The probe_timeout attribution is replaced
        // by the exhaustion classification.
        Assert.Equal("context_exhaustion", root.GetProperty("failureCategory").GetString());
        Assert.Equal(91d, root.GetProperty("contextUsagePercent").GetDouble());
    }

    [Fact]
    public void ApplyToPayload_MalformedJson_ReturnsNull()
    {
        var result = ContextExhaustionClassifier.ApplyToPayload(
            "{not-valid-json",
            new ContextExhaustionClassifier.ClassificationResult(
                Category: "context_exhaustion",
                ContextUsagePercent: 90d,
                IsSuspected: false,
                IsExhausted: true));

        Assert.Null(result);
    }
}
