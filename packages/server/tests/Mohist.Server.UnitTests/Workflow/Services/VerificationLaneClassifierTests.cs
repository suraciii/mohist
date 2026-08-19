using System.Text.Json;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.Services;

public sealed class VerificationLaneClassifierTests
{
    [Fact]
    public void IsRecognizedLaneTask_TrueForCatalogIds()
    {
        foreach (var laneId in VerificationLaneCatalog.LaneIds)
            Assert.True(VerificationLaneClassifier.IsRecognizedLaneTask(laneId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("verify")]
    [InlineData("recover:fix-ci")]
    [InlineData("workspace-prepare")]
    public void IsRecognizedLaneTask_FalseForUnknownOrLegacy(string? id)
    {
        Assert.False(VerificationLaneClassifier.IsRecognizedLaneTask(id));
    }

    [Fact]
    public void Classify_SuccessfulScriptWithoutAddTasks_IsPass()
    {
        var outcome = VerificationLaneClassifier.Classify(
            VerificationLaneCatalog.VerifyInstall,
            new TaskReport(
                WorkId: "verify-install.1",
                Status: TaskReportStatus.Succeeded,
                Output: null,
                Artifacts: null,
                Detail: null));

        Assert.Equal(VerificationLaneOutcome.Pass, outcome);
    }

    [Fact]
    public void Classify_SuccessfulScriptWithAddTasks_IsFailNotPass()
    {
        // The Runner's recovery scheduling envelope marks the task outer
        // status=completed but adds a follow-up task; that is a recovery
        // envelope, not a lane pass.
        var outcome = VerificationLaneClassifier.Classify(
            VerificationLaneCatalog.VerifyDotnet,
            new TaskReport(
                WorkId: "verify-dotnet.1",
                Status: TaskReportStatus.Succeeded,
                Output: null,
                Artifacts: null,
                AddTasks: new[]
                {
                    new RuntimeTaskInput(
                        Id: "recover:fix-ci",
                        Title: "Fix CI"),
                }));

        Assert.Equal(VerificationLaneOutcome.Fail, outcome);
    }

    [Fact]
    public void Classify_TimeoutRecoverySchedulingEnvelope_RemainsTimeout()
    {
        var outcome = VerificationLaneClassifier.Classify(
            VerificationLaneCatalog.VerifyDotnet,
            new TaskReport(
                WorkId: "verify-dotnet.1",
                Status: TaskReportStatus.Succeeded,
                Output: null,
                Artifacts: null,
                Detail: "Command exceeded its budget",
                AddTasks: new[]
                {
                    new RuntimeTaskInput("recover:fix-ci", "Fix CI"),
                    new RuntimeTaskInput(
                        VerificationLaneCatalog.VerifyDotnet,
                        "Verify dotnet",
                        "core/script",
                        RecoveryRemaining: 1),
                },
                Error: new ExecutionError("timeout", "Command exceeded its budget")));

        Assert.Equal(VerificationLaneOutcome.Timeout, outcome);
    }

    [Fact]
    public void Classify_NormalScriptFailure_IsFail()
    {
        var outcome = VerificationLaneClassifier.Classify(
            VerificationLaneCatalog.VerifyWebTypecheck,
            new TaskReport(
                WorkId: "verify-web-typecheck.1",
                Status: TaskReportStatus.Failed,
                Output: null,
                Artifacts: null,
                Detail: "tsc error",
                Error: new ExecutionError("script-failed", "tsc returned 1")));

        Assert.Equal(VerificationLaneOutcome.Fail, outcome);
    }

    [Fact]
    public void Classify_ErrorCodeTimeout_IsTimeout()
    {
        var outcome = VerificationLaneClassifier.Classify(
            VerificationLaneCatalog.VerifyWebTests,
            new TaskReport(
                WorkId: "verify-web-tests.1",
                Status: TaskReportStatus.Failed,
                Output: null,
                Artifacts: null,
                Detail: "killed by SIGTERM after budget",
                Error: new ExecutionError("timeout", "Command exceeded its 120000 ms budget")));

        Assert.Equal(VerificationLaneOutcome.Timeout, outcome);
    }

    [Fact]
    public void Classify_RecoverFixCiHelper_IsNull()
    {
        var outcome = VerificationLaneClassifier.Classify(
            "recover:fix-ci",
            new TaskReport(
                WorkId: "recover:fix-ci.1",
                Status: TaskReportStatus.Succeeded,
                Output: null,
                Artifacts: null,
                Detail: null));

        Assert.Null(outcome);
    }

    [Fact]
    public void Catalog_ContainsExactlySixStableIdsInDeclaredOrder()
    {
        Assert.Equal(new[]
        {
            "verify-install",
            "verify-dotnet",
            "verify-web-typecheck",
            "verify-web-tests",
            "verify-runner-typecheck",
            "verify-runner-tests",
        }, VerificationLaneCatalog.LaneIds);
    }
}