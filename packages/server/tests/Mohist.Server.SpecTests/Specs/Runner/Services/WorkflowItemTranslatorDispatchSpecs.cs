using System.Text.Json;
using Mohist.Server.Infrastructure;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.Workflow.Domain;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;


namespace Mohist.Server.SpecTests.Specs.Runner.Services;

public partial class WorkflowItemTranslatorSpecs
{
    [Fact]
    public async Task TranslateToDispatch_AgentTask_ComposesRawPromptAndConcreteRuntimeInput()
    {
        var runId = $"wr-{Guid.NewGuid():N}";
        var run = await SeedRunningWorkflowAsync(runId, "proj-agent-dispatch");
        _agentResolver.Snapshot = new AgentExecutionSnapshot(
            "Review the change.",
            JsonSerializer.SerializeToElement(new { runtime = "pi", model = "model-a", variant = "fast" }));

        var item = WorkItem.Task("build", "task-1.1", "Task 1", "mohist/agent", With("""
            { "name": "reviewer", "prompt": "Fix ${{ vars.target }}", "session": "review", "timeout": 123 }
            """));
        var dispatch = await _translator.TranslateToDispatchAsync(item, runId, run, "runner-1");

        Assert.Equal("mohist/pi", dispatch.Uses);
        using var with = JsonDocument.Parse(dispatch.With!);
        Assert.Equal("Review the change.\n\nFix ${{ vars.target }}", with.RootElement.GetProperty("prompt").GetString());
        Assert.Equal("review", with.RootElement.GetProperty("session").GetString());
        Assert.Equal(123, with.RootElement.GetProperty("timeout").GetInt32());
        var options = with.RootElement.GetProperty("options");
        Assert.Equal("model-a", options.GetProperty("model").GetString());
        Assert.Equal("fast", options.GetProperty("variant").GetString());
        Assert.False(options.TryGetProperty("instructions", out _));
        Assert.Equal(4, with.RootElement.EnumerateObject().Count());
        Assert.Equal("Fix ${{ vars.target }}", item.With!["prompt"]!.Value.GetString());
    }


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
    public async Task TranslateToDispatch_InlineAgentsRejectLegacyAgentInput(string uses)
    {
        var runId = $"wr-{Guid.NewGuid():N}";
        var run = await SeedRunningWorkflowAsync(runId, "proj-translate-inline");
        var item = WorkItem.Task("build", "task-1.1", "Task 1", uses,
            With(@"{ ""agent"": ""legacy"" }"));

        var error = await Assert.ThrowsAsync<WorkflowDispatchRejectedException>(
            () => _translator.TranslateToDispatchAsync(item, runId, run, "runner-1"));

        Assert.Contains("with.agent", error.Message, StringComparison.Ordinal);
        Assert.Equal("invalid_input", error.Error.Code);
    }

    [Fact]
    public async Task TranslateToDispatch_AgentTask_MissingAgent_RejectsWithAgentNotFound()
    {
        var runId = $"wr-{Guid.NewGuid():N}";
        var run = await SeedRunningWorkflowAsync(runId, "proj-agent-missing");
        _agentResolver.Snapshot = null;

        var item = WorkItem.Task("build", "task-1.1", "Task 1", "mohist/agent",
            With(@"{ ""name"": ""archived-reviewer"", ""prompt"": ""Review the change."" }"));

        var error = await Assert.ThrowsAsync<WorkflowDispatchRejectedException>(
            () => _translator.TranslateToDispatchAsync(item, runId, run, "runner-1"));

        Assert.Equal("agent_not_found", error.Error.Code);
        Assert.Contains("archived-reviewer", error.Error.Message, StringComparison.Ordinal);
        Assert.Contains("archived-reviewer", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TranslateToDispatch_AgentTask_AgentResolverUnavailable_RejectsWithAgentNotFound()
    {
        var runId = $"wr-{Guid.NewGuid():N}";
        var run = await SeedRunningWorkflowAsync(runId, "proj-agent-no-resolver");
        var translatorWithoutResolver = new WorkflowItemTranslator(
            _profileManager, _bindService, TranslatorNullLogger, agentSnapshots: null);

        var item = WorkItem.Task("build", "task-1.1", "Task 1", "mohist/agent",
            With(@"{ ""name"": ""reviewer"", ""prompt"": ""Review the change."" }"));

        var error = await Assert.ThrowsAsync<WorkflowDispatchRejectedException>(
            () => translatorWithoutResolver.TranslateToDispatchAsync(item, runId, run, "runner-1"));

        Assert.Equal("agent_not_found", error.Error.Code);
    }

    [Fact]
    public async Task TranslateToDispatch_AgentTask_MalformedInput_RejectsWithInvalidAgentInput()
    {
        var runId = $"wr-{Guid.NewGuid():N}";
        var run = await SeedRunningWorkflowAsync(runId, "proj-agent-malformed");
        _agentResolver.Snapshot = new AgentExecutionSnapshot(
            "Review the change.",
            JsonSerializer.SerializeToElement(new { runtime = "opencode", model = "model-a" }));

        var item = WorkItem.Task("build", "task-1.1", "Task 1", "mohist/agent",
            With(@"{ ""name"": ""  "", ""prompt"": ""Review the change."" }"));

        var error = await Assert.ThrowsAsync<WorkflowDispatchRejectedException>(
            () => _translator.TranslateToDispatchAsync(item, runId, run, "runner-1"));

        Assert.Equal("invalid_agent_input", error.Error.Code);
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
