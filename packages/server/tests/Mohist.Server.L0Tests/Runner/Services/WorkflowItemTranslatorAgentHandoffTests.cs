using System.Text.Json;
using Mohist.Server.Runner.Services;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.L0Tests.Runner.Services;

public partial class WorkflowItemTranslatorSpecs
{
    [Fact]
    public async Task BuildAgentHandoffCommand_WithoutTimeout_LeavesTimeoutUnset()
    {
        var runId = $"wr-agent-default-timeout-{Guid.NewGuid():N}";
        var input = With("""{"name":"mohist/builder","prompt":"build"}""");
        var run = await SeedRunningWorkflowAsync(
            runId,
            "proj-agent-default-timeout",
            taskDefinition: new TaskDefinition("task-1", "Agent", "mohist/agent", input));
        var item = WorkItem.Task("build", "task-1.1", "Agent", "mohist/agent", input);

        var command = await _translator.BuildAgentHandoffCommandAsync(item, runId, run);

        Assert.Null(command.TimeoutMilliseconds);
    }

    [Fact]
    public async Task BuildAgentHandoffCommand_NamedSession_ReusesEarlierAgentSession()
    {
        var runId = $"wr-agent-reuse-{Guid.NewGuid():N}";
        var input = With("""{"name":"mohist/builder","prompt":"continue","session":"delivery","timeout":45000}""");
        var run = await SeedRunningWorkflowAsync(
            runId,
            "proj-agent-reuse",
            taskDefinition: new TaskDefinition("task-1", "Agent", "mohist/agent", input));
        run.CurrentStage().Tasks.Insert(0, PreviousAgentAttempt("mohist/builder", "delivery", "session-1"));
        var item = WorkItem.Task("build", "task-1.1", "Agent", "mohist/agent", input);

        var command = await _translator.BuildAgentHandoffCommandAsync(item, runId, run);

        Assert.Equal("session-1", command.ReuseSessionId);
        Assert.Equal("delivery", command.Session);
        Assert.Equal(45_000, command.TimeoutMilliseconds);
    }

    [Fact]
    public async Task BuildAgentHandoffCommand_NamedSession_RejectsAgentSwitch()
    {
        var runId = $"wr-agent-conflict-{Guid.NewGuid():N}";
        var input = With("""{"name":"mohist/reviewer","prompt":"review","session":"delivery"}""");
        var run = await SeedRunningWorkflowAsync(
            runId,
            "proj-agent-conflict",
            taskDefinition: new TaskDefinition("task-1", "Agent", "mohist/agent", input));
        run.CurrentStage().Tasks.Insert(0, PreviousAgentAttempt("mohist/builder", "delivery", "session-1"));
        var item = WorkItem.Task("build", "task-1.1", "Agent", "mohist/agent", input);

        var error = await Assert.ThrowsAsync<WorkflowDispatchRejectedException>(
            () => _translator.BuildAgentHandoffCommandAsync(item, runId, run));

        Assert.Equal("workflow_session_agent_conflict", error.Error.Code);
    }

    private static WorkflowActionAttempt PreviousAgentAttempt(string agent, string session, string agentSessionId) => new()
    {
        Id = "previous.1",
        DefinitionId = "previous",
        Attempt = 1,
        Title = "Previous Agent turn",
        Uses = "mohist/agent",
        WithInput = new Dictionary<string, JsonElement?>
        {
            ["name"] = JsonSerializer.SerializeToElement(agent),
            ["session"] = JsonSerializer.SerializeToElement(session),
            ["prompt"] = JsonSerializer.SerializeToElement("start"),
        },
        Status = WorkflowActionAttemptStatus.Completed,
        AgentJobId = "job-1",
        AgentSessionId = agentSessionId,
    };
}
