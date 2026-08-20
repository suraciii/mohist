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


namespace Mohist.Server.UnitTests.Runner.Services;

public partial class WorkflowItemTranslatorSpecs
{
    [Theory]
    [InlineData("opencode", "mohist/opencode")]
    [InlineData("pi", "mohist/pi")]
    public async Task TranslateToDispatch_AgentTask_ComposesRawPromptAndConcreteRuntimeInput(
        string runtime,
        string expectedUses)
    {
        var runId = $"wr-{Guid.NewGuid():N}";
        var run = await SeedRunningWorkflowAsync(runId, "proj-agent-dispatch");
        _agentResolver.Snapshot = new AgentExecutionDefinition(
            "Review the change.", runtime, "model-a", "fast", ["mohist", "review"]);

        var item = WorkItem.Task("build", "task-1.1", "Task 1", "mohist/agent", With("""
            { "name": "reviewer", "prompt": "Fix ${{ vars.target }}", "session": "review", "timeout": 123 }
            """));
        var dispatch = await _translator.TranslateToDispatchAsync(item, runId, run, "runner-1");

        Assert.Equal(expectedUses, dispatch.Uses);
        Assert.Equal(WorkDispatchOwnerKinds.Workflow, dispatch.OwnerKind);
        Assert.Null(dispatch.AgentJobId);
        using var with = JsonDocument.Parse(dispatch.With!);
        Assert.Equal("Fix ${{ vars.target }}", with.RootElement.GetProperty("prompt").GetString());
        Assert.Equal("review", with.RootElement.GetProperty("session").GetString());
        Assert.Equal(123, with.RootElement.GetProperty("timeout").GetInt32());
        Assert.Equal(3, with.RootElement.EnumerateObject().Count());
        Assert.Equal("Review the change.", dispatch.AgentDefinition!.Instructions);
        Assert.Equal(runtime, dispatch.AgentDefinition.Runtime);
        Assert.Equal("model-a", dispatch.AgentDefinition.Model);
        Assert.Equal("fast", dispatch.AgentDefinition.Variant);
        Assert.Equal(["mohist", "review"], dispatch.AgentDefinition.Skills);
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
    public async Task TranslateToDispatch_AgentTask_ForwardsEffectiveAgentOptions()
    {
        var runId = $"wr-{Guid.NewGuid():N}";
        var projectId = "proj-agent-effective-options";
        var run = await SeedRunningWorkflowAsync(runId, projectId);
        var factory = new TestDbContextFactory(_database.Options);
        var issueVariables = new IssueVariableStore(factory);
        await issueVariables.SetVariablesAsync(projectId, 42, new VariableBundle(
            Vars: JsonSerializer.SerializeToElement(new
            {
                agent = new
                {
                    model = "issue-model",
                    variant = "issue-variant",
                    reasoningEffort = "high",
                },
            })));

        _agentResolver.Snapshot = new AgentExecutionDefinition(
            "Review the change.", "pi", null, null, []);
        var item = WorkItem.Task("build", "task-1.1", "Task 1", "mohist/agent",
            With("""{"name":"reviewer","prompt":"Review the change."}"""));

        var dispatch = await _translator.TranslateToDispatchAsync(item, runId, run, "runner-1");

        using var with = JsonDocument.Parse(dispatch.With!);
        var options = with.RootElement.GetProperty("options");
        Assert.Equal("issue-model", options.GetProperty("model").GetString());
        Assert.Equal("issue-variant", options.GetProperty("variant").GetString());
        Assert.Equal("high", options.GetProperty("reasoningEffort").GetString());
        Assert.DoesNotContain("runtime", options.EnumerateObject().Select(property => property.Name));
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
    public async Task TranslateToDispatch_AgentTask_FreezesReasoningEffortOnTheDispatchSnapshot()
    {
        // Issue-557 T-002: the workflow-task launch path freezes the
        // canonical effort member of the execution tuple onto the
        // dispatch's AgentDefinition beside model and variant. The
        // snapshot is resolved once at translation time; a later Agent
        // edit cannot rewrite the delivered dispatch.
        var runId = $"wr-{Guid.NewGuid():N}";
        var run = await SeedRunningWorkflowAsync(runId, "proj-agent-effort");
        _agentResolver.Snapshot = new AgentExecutionDefinition(
            "Review the change.", "opencode", "model-a", "balanced",
            ["mohist"], AllowedSubagents: null, ReasoningEffort: "high");

        var item = WorkItem.Task("build", "task-1.1", "Task 1", "mohist/agent",
            With("""{"name":"reviewer","prompt":"Review the change."}"""));
        var dispatch = await _translator.TranslateToDispatchAsync(item, runId, run, "runner-1");

        Assert.Equal("high", dispatch.AgentDefinition!.ReasoningEffort);
        Assert.Equal("model-a", dispatch.AgentDefinition.Model);
        Assert.Equal("balanced", dispatch.AgentDefinition.Variant);
    }

    [Fact]
    public async Task TranslateToDispatch_AgentTask_LeavesEffortUnset_WhenSnapshotCarriesNone()
    {
        var runId = $"wr-{Guid.NewGuid():N}";
        var run = await SeedRunningWorkflowAsync(runId, "proj-agent-no-effort");
        _agentResolver.Snapshot = new AgentExecutionDefinition(
            "Review the change.", "opencode", "model-a", "fast", []);

        var item = WorkItem.Task("build", "task-1.1", "Task 1", "mohist/agent",
            With("""{"name":"reviewer","prompt":"Review the change."}"""));
        var dispatch = await _translator.TranslateToDispatchAsync(item, runId, run, "runner-1");

        Assert.Null(dispatch.AgentDefinition!.ReasoningEffort);
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
    public async Task TranslateToDispatch_AgentTask_RetryResolutionUsesCurrentSnapshot()
    {
        var runId = $"wr-{Guid.NewGuid():N}";
        var run = await SeedRunningWorkflowAsync(runId, "proj-agent-retry");
        var item = WorkItem.Task("build", "task-1.1", "Task 1", "mohist/agent",
            With("""{"name":"reviewer","prompt":"Review the change."}"""));

        _agentResolver.Snapshot = new AgentExecutionDefinition(
            "Original instructions", "opencode", null, null, []);
        var first = await _translator.TranslateToDispatchAsync(item, runId, run, "runner-1");

        var retryRunId = $"wr-{Guid.NewGuid():N}";
        var retryRun = await SeedRunningWorkflowAsync(retryRunId, "proj-agent-retry-current", "task-1.2");
        _agentResolver.Snapshot = new AgentExecutionDefinition(
            "Edited instructions", "opencode", null, null, []);
        var retried = await _translator.TranslateToDispatchAsync(
            item with { Id = "task-1.2" }, retryRunId, retryRun, "runner-1");

        Assert.Equal("Original instructions", first.AgentDefinition!.Instructions);
        Assert.Equal("Edited instructions", retried.AgentDefinition!.Instructions);
        Assert.NotEqual(first.WorkId, retried.WorkId);
    }

    [Fact]
    public async Task TranslateToDispatch_AgentTask_EmptySkillsOmitsSkillInput()
    {
        var runId = $"wr-{Guid.NewGuid():N}";
        var run = await SeedRunningWorkflowAsync(runId, "proj-agent-empty-skills");
        _agentResolver.Snapshot = new AgentExecutionDefinition(
            "Keep the response concise.", "opencode", null, null, []);

        var item = WorkItem.Task("build", "task-1.1", "Task 1", "mohist/agent",
            With("""{"name":"reviewer","prompt":"Review the change."}"""));

        var dispatch = await _translator.TranslateToDispatchAsync(item, runId, run, "runner-1");

        using var with = JsonDocument.Parse(dispatch.With!);
        Assert.Equal("Review the change.", with.RootElement.GetProperty("prompt").GetString());
        Assert.Equal("Keep the response concise.", dispatch.AgentDefinition!.Instructions);
        Assert.Empty(dispatch.AgentDefinition.Skills);
    }

    [Fact]
    public async Task TranslateToDispatch_AgentTask_AgentResolverUnavailable_RejectsWithAgentNotFound()
    {
        var runId = $"wr-{Guid.NewGuid():N}";
        var run = await SeedRunningWorkflowAsync(runId, "proj-agent-no-resolver");
        var translatorWithoutResolver = new WorkflowItemTranslator(
            _promptResolver, _variableResolver, agentSnapshots: null);

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
        _agentResolver.Snapshot = new AgentExecutionDefinition(
            "Review the change.", "opencode", "model-a", null, []);

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
