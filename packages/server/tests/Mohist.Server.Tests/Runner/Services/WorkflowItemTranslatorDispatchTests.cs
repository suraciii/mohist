using System.Text.Json;
using Mohist.Server.Infrastructure;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.Workflow.Domain;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
using Mohist.Server.TestSupport;
using Xunit;


namespace Mohist.Server.Tests.Runner.Services;

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
    public async Task TranslateToDispatch_StageAgentValuesOverrideProjectAndIssueTopLevelValues()
    {
        var runId = $"wr-{Guid.NewGuid():N}";
        var projectId = "proj-translate-stage-agent";
        var run = await SeedRunningWorkflowAsync(runId, projectId);
        var factory = new TestDbContextFactory(_database.Options);
        var projectVariables = new ProjectVariableStore(factory);
        var issueVariables = new IssueVariableStore(factory);

        await projectVariables.SetVariablesAsync(projectId, new VariableBundle(
            Vars: JsonSerializer.SerializeToElement(new
            {
                agent = new { model = "old-project-model", variant = "old-project-variant" },
            }),
            Stages: new Dictionary<string, StageVariables>(StringComparer.OrdinalIgnoreCase)
            {
                ["build"] = new(JsonSerializer.SerializeToElement(new
                {
                    agent = new { model = "stage-model", variant = "stage-variant" },
                })),
            }));
        await issueVariables.SetVariablesAsync(projectId, 42, new VariableBundle(
            Vars: JsonSerializer.SerializeToElement(new
            {
                agent = new { model = "old-issue-model", variant = "old-issue-variant", reasoningEffort = "low" },
            })));

        var item = WorkItem.Task("build", "task-1.1", "Task 1", "spec/task",
            With(@"{ ""options"": ""${{ vars.agent }}"" }"));
        var dispatch = await _translator.TranslateToDispatchAsync(item, runId, run, "runner-1");

        using var document = JsonDocument.Parse(dispatch.Variables!);
        var agent = document.RootElement.GetProperty("vars").GetProperty("agent");
        Assert.Equal("stage-model", agent.GetProperty("model").GetString());
        Assert.Equal("stage-variant", agent.GetProperty("variant").GetString());
        Assert.Equal("low", agent.GetProperty("reasoningEffort").GetString());
    }

    [Fact]
    public async Task TranslateToDispatch_TaskItem_UsesOnlyClosedRootsAndDoesNotHoistVariables()
    {
        var runId = $"wr-{Guid.NewGuid():N}";
        var run = await SeedRunningWorkflowAsync(runId, "proj-translate-roots");
        var dispatch = await _translator.TranslateToDispatchAsync(
            WorkItem.Task("build", "task-1.1", "Task 1", "spec/task",
                With(@"{ ""custom"": ""value"" }")),
            runId, run, "runner-1");

        using var doc = JsonDocument.Parse(dispatch.Variables!);
        var roots = doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Subset(
            new HashSet<string>(["workflow", "stage", "work", "issue", "repository", "workspace", "vars"], StringComparer.Ordinal),
            roots);
        Assert.DoesNotContain("custom", roots);
        Assert.DoesNotContain("mohist", roots);
        Assert.DoesNotContain("project", roots);
        Assert.DoesNotContain("approvalFeedback", roots);
        Assert.DoesNotContain("runner", roots);
        Assert.Empty(doc.RootElement.GetProperty("vars").EnumerateObject());
    }

    [Theory]
    [InlineData("mohist/opencode")]
    [InlineData("mohist/pi")]
    public async Task TranslateToDispatch_AgentActionsRejectLegacyAgentInput(string uses)
    {
        var runId = $"wr-{Guid.NewGuid():N}";
        var run = await SeedRunningWorkflowAsync(runId, "proj-translate-inline");
        var item = WorkItem.Task("build", "task-1.1", "Task 1", uses,
            With(@"{ ""agent"": ""legacy"" }"));

        var error = await Assert.ThrowsAsync<WorkflowDispatchRejectedException>(
            () => _translator.TranslateToDispatchAsync(item, runId, run, "runner-1"));

        Assert.Equal("removed_agent_action", error.Error.Code);
        Assert.Contains("mohist/agent", error.Error.Message, StringComparison.Ordinal);
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
