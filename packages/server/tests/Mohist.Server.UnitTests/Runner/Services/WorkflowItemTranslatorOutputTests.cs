using Mohist.Server.Infrastructure;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.Workflow.Domain;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;

namespace Mohist.Server.UnitTests.Runner.Services;

public partial class WorkflowItemTranslatorSpecs
{
    [Fact]
    public async Task TranslateResult_InvalidTaskOutput_DoesNotBindArtifacts()
    {
        var runId = $"wr-{Guid.NewGuid():N}";
        var projectId = "proj-result-invalid-output";
        await SeedRunningWorkflowAsync(runId, projectId);
        var item = WorkItem.Task("build", "task-1.1", "Task 1", "spec/task", null,
            artifacts: new TaskArtifactCapture([new TaskArtifactDeclaration("review.md")]));
        var result = new WorkResult("completed", Output: JSON.DeserializeElement("\"not-an-object\""), ArtifactUploadIds: ["missing-upload"]);

        var report = _translator.TranslateResult(item, result, runId);

        var task = Assert.IsType<WorkflowItemTranslator.InboundReport.Task>(report);
        Assert.Equal(TaskReportStatus.Failed, task.Value.Status);
        Assert.Null(task.Value.Artifacts);
        Assert.Equal("unexpected-error", task.Value.Error?.Code);
    }

    [Theory]
    [InlineData("\"bad\"")]
    [InlineData("42")]
    [InlineData("null")]
    public async Task TranslateResult_MalformedChecksOutput_FailsEveryDispatchedCheck(string output)
    {
        var runId = $"wr-{Guid.NewGuid():N}";
        var projectId = "proj-result-malformed-checks";
        await SeedRunningWorkflowAsync(runId, projectId);
        var item = WorkItem.Checks("build", "checks-build",
            [new CheckItem("check-1", "Check 1", "spec/check")]);

        var report = _translator.TranslateResult(
            item, new WorkResult("fail", Output: JSON.DeserializeElement(output)), runId);

        var checks = Assert.IsType<WorkflowItemTranslator.InboundReport.Checks>(report);
        var check = Assert.Single(checks.Value.Results);
        Assert.Equal("check-1", check.Name);
        Assert.Equal(CheckResultStatus.Failed, check.Status);
        Assert.Equal("unexpected-error", check.Error?.Code);
    }

    [Theory]
    [InlineData("[42]")]
    [InlineData("[null]")]
    [InlineData("""[{"name":"check-1","status":"pass","output":"bad"}]""")]
    [InlineData("""[{"name":"check-1","status":1,"output":null}]""")]
    [InlineData("""[{"name":"check-1","status":"pass","message":false,"output":null}]""")]
    [InlineData("""[{"name":"check-1","status":"pass","error":{"code":1,"message":"bad"},"output":null}]""")]
    [InlineData("""[{"name":"check-1","status":"pass","error":{"code":"bad","message":false},"output":null}]""")]
    public async Task TranslateResult_MalformedCheckRow_FailsEveryDispatchedCheck(string output)
    {
        var runId = $"wr-{Guid.NewGuid():N}";
        var projectId = "proj-result-malformed-check-row";
        await SeedRunningWorkflowAsync(runId, projectId);
        var item = WorkItem.Checks("build", "checks-build",
            [new CheckItem("check-1", "Check 1", "spec/check")]);

        var report = _translator.TranslateResult(
            item, new WorkResult("fail", Output: JSON.DeserializeElement(output)), runId);

        var checks = Assert.IsType<WorkflowItemTranslator.InboundReport.Checks>(report);
        var check = Assert.Single(checks.Value.Results);
        Assert.Equal("check-1", check.Name);
        Assert.Equal(CheckResultStatus.Failed, check.Status);
        Assert.Equal("unexpected-error", check.Error?.Code);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""[{"name":"unknown","status":"pass","output":null},{"name":"check-1","status":"pass","output":null}]""")]
    [InlineData("""[{"name":"check-1","status":"pass","output":null}]""")]
    [InlineData("""[{"name":"check-1","status":"pass","output":null},{"name":"check-1","status":"pass","output":null}]""")]
    public async Task TranslateResult_CheckSetMismatch_FailsEveryDispatchedCheck(string output)
    {
        var runId = $"wr-{Guid.NewGuid():N}";
        var projectId = "proj-result-check-set-mismatch";
        await SeedRunningWorkflowAsync(runId, projectId);
        var item = WorkItem.Checks("build", "checks-build",
            [new CheckItem("check-1", "Check 1", "spec/check"), new CheckItem("check-2", "Check 2", "spec/check")]);

        var report = _translator.TranslateResult(
            item, new WorkResult("fail", Output: JSON.DeserializeElement(output)), runId);

        var checks = Assert.IsType<WorkflowItemTranslator.InboundReport.Checks>(report);
        Assert.Equal(["check-1", "check-2"], checks.Value.Results.Select(check => check.Name));
        Assert.All(checks.Value.Results, check =>
        {
            Assert.Equal(CheckResultStatus.Failed, check.Status);
            Assert.Equal("unexpected-error", check.Error?.Code);
        });
    }
}
