using System.Text.Json;
using Mohist.Server.Infrastructure;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.Workflow.Domain;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Mohist.Server.TestSupport;
using Xunit;


namespace Mohist.Server.SpecTests.Specs.Runner.Services;

public partial class WorkflowItemTranslatorSpecs
{
    [Fact]
    public async Task TranslateToDispatch_AgentTask_ReturnsDelegatedHandoffWithPersistedInput()
    {
        var runId = $"wr-{Guid.NewGuid():N}";
        var projectId = "proj-agent-dispatch";
        var run = await SeedRunningWorkflowAsync(runId, projectId);
        var item = WorkItem.Task("build", "task-1.1", "Task 1", "mohist/agent", With("""
            { "name": "reviewer", "prompt": "Fix ${{ vars.target }}", "session": "review", "timeout": 123 }
            """), expect: With("{\"markers\":[{\"path\":\"_output\",\"contains\":\"done\"}]}"));

        var result = await _translator.TranslateToDispatchAsync(item, runId, run, "runner-1");
        var delegated = Assert.IsType<WorkflowItemTranslationResult.Delegated>(result);
        var command = Assert.Single(_handoff.Commands);

        Assert.Equal(projectId, command.ProjectId);
        Assert.Equal(runId, command.WorkflowRunId);
        Assert.Equal("task-1.1", command.TaskRunId);
        Assert.Equal("task-1.1", command.CommandId);
        Assert.Equal("reviewer", command.AgentRef);
        Assert.Equal("Fix ${{ vars.target }}", command.Prompt);
        Assert.Equal("review", command.Session);
        Assert.Equal(123, command.TimeoutMilliseconds);
        Assert.Equal(JSON.Serialize(item.Expect), command.Expect);
        Assert.Equal(WorkflowAgentHandoffCodec.InvocationFor(command), delegated.Invocation);
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

        var dispatch = Dispatch(await _translator.TranslateToDispatchAsync(item, runId, run, "runner-1"));

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
                agent = new { model = "old-issue-model", variant = "old-issue-variant" },
            })));

        var item = WorkItem.Task("build", "task-1.1", "Task 1", "spec/task",
            With(@"{ ""options"": ""${{ vars.agent }}"" }"));
        var dispatch = Dispatch(await _translator.TranslateToDispatchAsync(item, runId, run, "runner-1"));

        using var document = JsonDocument.Parse(dispatch.Variables!);
        var agent = document.RootElement.GetProperty("vars").GetProperty("agent");
        Assert.Equal("stage-model", agent.GetProperty("model").GetString());
        Assert.Equal("stage-variant", agent.GetProperty("variant").GetString());
    }

    [Fact]
    public async Task TranslateToDispatch_TaskItem_UsesOnlyClosedRootsAndDoesNotHoistVariables()
    {
        var runId = $"wr-{Guid.NewGuid():N}";
        var run = await SeedRunningWorkflowAsync(runId, "proj-translate-roots");
        var dispatch = Dispatch(await _translator.TranslateToDispatchAsync(
            WorkItem.Task("build", "task-1.1", "Task 1", "spec/task",
                With(@"{ ""custom"": ""value"" }")),
            runId, run, "runner-1"));

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
        _handoff.Rejection = new WorkflowAgentHandoffRejection(
            "agent_not_found",
            "Workflow Agent handoff references Agent 'archived-reviewer' which does not exist or is archived.");

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
        var first = Assert.IsType<WorkflowItemTranslationResult.Delegated>(await _translator.TranslateToDispatchAsync(item, runId, run, "runner-1"));

        var retryRunId = $"wr-{Guid.NewGuid():N}";
        var retryRun = await SeedRunningWorkflowAsync(retryRunId, "proj-agent-retry-current", "task-1.2");
        _agentResolver.Snapshot = new AgentExecutionDefinition(
            "Edited instructions", "opencode", null, null, []);
        var retried = Assert.IsType<WorkflowItemTranslationResult.Delegated>(await _translator.TranslateToDispatchAsync(
            item with { Id = "task-1.2" }, retryRunId, retryRun, "runner-1"));

        Assert.Equal("task-1.1", first.Invocation.CommandId);
        Assert.Equal("task-1.2", retried.Invocation.CommandId);
        Assert.NotEqual(first.Invocation.InvocationId, retried.Invocation.InvocationId);
        Assert.Equal(2, _handoff.Commands.Count);
    }

    [Fact]
    public async Task TranslateToDispatch_AgentTask_ReplayedInputReusesInvocationIdentity()
    {
        var runId = $"wr-{Guid.NewGuid():N}";
        var run = await SeedRunningWorkflowAsync(runId, "proj-agent-replay");
        var item = WorkItem.Task("build", "task-1.1", "Task 1", "mohist/agent",
            With("""{"name":"reviewer","prompt":"Review the change."}"""));

        var first = Assert.IsType<WorkflowItemTranslationResult.Delegated>(
            await _translator.TranslateToDispatchAsync(item, runId, run, "runner-1"));
        var replay = Assert.IsType<WorkflowItemTranslationResult.Delegated>(
            await _translator.TranslateToDispatchAsync(item, runId, run, "runner-1"));

        Assert.Equal(first.Invocation, replay.Invocation);
        Assert.Equal(2, _handoff.Commands.Count);
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
    public async Task TranslateToDispatch_AgentTask_DurableInvalidInputKeepsExistingRejectionCode()
    {
        var runId = $"wr-{Guid.NewGuid():N}";
        var run = await SeedRunningWorkflowAsync(runId, "proj-agent-invalid-timeout");
        _handoff.Rejection = new WorkflowAgentHandoffRejection(
            "invalid_agent_input",
            "Workflow Agent handoff timeout must be positive when supplied.");
        var item = WorkItem.Task("build", "task-1.1", "Task 1", "mohist/agent",
            With("""{"name":"reviewer","prompt":"Review the change.","timeout":0}"""));

        var error = await Assert.ThrowsAsync<WorkflowDispatchRejectedException>(
            () => _translator.TranslateToDispatchAsync(item, runId, run, "runner-1"));

        Assert.Equal("invalid_agent_input", error.Error.Code);
        Assert.Contains("timeout", error.Message, StringComparison.Ordinal);
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

        var dispatch = Dispatch(await _translator.TranslateToDispatchAsync(item, runId, run, "runner-1"));

        var check = JsonDocument.Parse(dispatch.With!).RootElement
            .GetProperty("checks")[0]
            .GetProperty("with")
            .GetProperty("path");
        Assert.Equal("${{ vars.reviewPath }}", check.GetString());
    }
}
