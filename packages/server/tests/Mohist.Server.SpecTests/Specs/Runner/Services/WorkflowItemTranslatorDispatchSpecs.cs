using System.Text.Json;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Runner.Services;

public partial class WorkflowItemTranslatorSpecs
{
    [Fact]
    public async Task TranslateToDispatch_TaskItem_PreservesRawDeclarationsAlongsideSnapshot()
    {
        var runId = $"wr-{Guid.NewGuid():N}";
        var projectId = "proj-translate-1";
        var run = await SeedRunningWorkflowAsync(runId, projectId);
        var item = WorkItem.Task("build", "task-1.1", "Task 1", "spec/task",
            With(@"{ ""options"": ""${{ vars.agent }}"" }"),
            artifacts: new TaskArtifactCapture([new TaskArtifactDeclaration("review.md")]),
            setVars: new Dictionary<string, string> { ["out"] = "answer" },
            expect: With(@"{ ""marker"": ""${{ vars.marker }}"" }"));

        var dispatch = await _translator.TranslateToDispatchAsync(item, runId, run, "runner-1");

        Assert.Equal(runId, dispatch.WorkflowRunId);
        Assert.Equal("task-1.1", dispatch.WorkId);
        Assert.Equal("task", dispatch.WorkType);
        Assert.Equal("build", dispatch.Stage);
        Assert.Equal("spec/task", dispatch.Uses);
        Assert.Equal(WorkDispatchOwnerKinds.Workflow, dispatch.OwnerKind);
        Assert.NotNull(dispatch.With);
        Assert.NotNull(dispatch.Variables);
        Assert.NotNull(dispatch.Artifacts);
        Assert.NotNull(dispatch.SetVars);
        Assert.Equal(7, dispatch.EpicNumber);
        Assert.Equal("${{ vars.agent }}", JsonDocument.Parse(dispatch.With!).RootElement.GetProperty("options").GetString());
        Assert.Equal("${{ vars.marker }}", JsonDocument.Parse(dispatch.Expect!).RootElement.GetProperty("marker").GetString());
        Assert.DoesNotContain("model-a", dispatch.With, StringComparison.Ordinal);
        Assert.True(JsonDocument.Parse(dispatch.Variables!).RootElement.TryGetProperty("vars", out _));
    }

    [Fact]
    public async Task TranslateToDispatch_ChecksItem_PreservesCheckTemplates()
    {
        var runId = $"wr-{Guid.NewGuid():N}";
        var projectId = "proj-translate-check-raw";
        var run = await SeedRunningWorkflowAsync(runId, projectId);
        var item = WorkItem.Checks("build", "checks-build", [
            new CheckItem("check-1", "Check 1", "spec/check",
                With(@"{ ""path"": ""${{ vars.reviewPath }}"" }")),
        ]);

        var dispatch = await _translator.TranslateToDispatchAsync(item, runId, run, "runner-1");

        var check = JsonDocument.Parse(dispatch.With!).RootElement
            .GetProperty("checks")[0]
            .GetProperty("with")
            .GetProperty("path");
        Assert.Equal("${{ vars.reviewPath }}", check.GetString());
    }
}
