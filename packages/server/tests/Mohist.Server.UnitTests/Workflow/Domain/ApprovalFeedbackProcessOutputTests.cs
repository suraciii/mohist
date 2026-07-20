using System.Text.Json;
using Mohist.Server.Infrastructure;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.Domain;

public partial class ApprovalFeedbackTests
{
    [Theory]
    [InlineData("Addressed the requested changes", "Addressed the requested changes")]
    [InlineData("  ## Feedback Resolution\n\nAddressed the requested changes\n\n## Verification\nnpm test passed  ", "Addressed the requested changes")]
    [InlineData("## Feedback Resolution\n\n## Verification\nnpm test passed", null)]
    public void ResolveFeedback_CoreProcessOutput_AdaptsResolutionSummary(string stdout, string? expectedSummary)
    {
        var output = JSON.SerializeToElement(new { stdout, exitCode = 0 });

        var resolved = ResolveConfiguredFeedback("core/process", output);

        Assert.NotNull(resolved);
        Assert.Equal(expectedSummary, resolved!.ResolutionSummary);
    }

    [Fact]
    public void ResolveFeedback_NullOrNonAdaptedOutput_LeavesSummaryNull()
    {
        var nullOutput = ResolveConfiguredFeedback("core/process", null);
        var nonAdaptedOutput = ResolveConfiguredFeedback("mohist/opencode", JSON.SerializeToElement(new { stdout = "ignored", exitCode = 0 }));

        Assert.Null(nullOutput!.ResolutionSummary);
        Assert.Null(nonAdaptedOutput!.ResolutionSummary);
    }

    private static ApprovalFeedback? ResolveConfiguredFeedback(string uses, JsonElement? output)
    {
        var run = BuildAwaitingApprovalRun();
        var feedbackId = NextFeedbackId(run);
        run.RequestChanges("apply feedback", feedbackId, DateTimeOffset.UnixEpoch,
            [new TaskDefinition("apply-feedback", "Apply approval feedback", uses)]);
        var task = run.CurrentStage().Tasks.Last(t => t.CausedByFeedbackId == feedbackId);
        run.StartTask(task.Id, "worker-1", DateTimeOffset.UnixEpoch);
        task.Output = output;
        run.CompleteTask(DateTimeOffset.UnixEpoch);

        return run.ResolveFeedback(feedbackId, task.Id, output, DateTimeOffset.UnixEpoch);
    }
}
