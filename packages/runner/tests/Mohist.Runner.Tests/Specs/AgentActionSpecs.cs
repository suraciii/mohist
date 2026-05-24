using Mohist.Runner.Actions;
using Mohist.Runner.Transport;
using Xunit;

namespace Mohist.Runner.Tests.Specs;

public class AgentActionSpecs
{
    [Fact]
    public async Task AgentAction_WithFakeExecutor_BuildsRequestAndCompletes()
    {
        using var temp = new TempDir();
        var executor = new FakeAgentExecutor(new AgentExecutionResult(0, "done"));
        var action = new AgentAction(executor);

        var result = await action.ExecuteAsync(SpecHelpers.Context(
            temp.Path,
            "task",
            "mohist/agent",
            new { stage = "plan", task = "proposal", changeDir = "openspec/changes/1-test" }));

        Assert.Equal("success", result.Status);
        Assert.NotNull(executor.Request);
        Assert.Equal("plan", executor.Request.Stage);
        Assert.Equal("proposal", executor.Request.Task);
        Assert.Equal(temp.Path, executor.Request.WorkDir);
        Assert.EndsWith(Path.Combine("openspec", "changes", "1-test"), executor.Request.ChangeDir);
        Assert.Contains("Stage: plan", executor.Request.Prompt);
        Assert.Contains("Task: proposal", executor.Request.Prompt);
        Assert.Contains("Output Contract: Proposal", executor.Request.Prompt);
        Assert.Contains("proposal.md", executor.Request.Prompt);
        Assert.Contains("agent", result.Output);
    }

    [Fact]
    public async Task AgentAction_WithVariables_RendersIssueAndStageModel()
    {
        using var temp = new TempDir();
        var executor = new FakeAgentExecutor(new AgentExecutionResult(0, "done"));
        var action = new AgentAction(executor);

        var result = await action.ExecuteAsync(SpecHelpers.Context(
            temp.Path,
            "task",
            "mohist/agent",
            new { stage = "build", task = "T-001", changeDir = "openspec/changes/1-test" },
            new
            {
                issue = new { number = 7, title = "Add sessions", body = "Track agent activity" },
                project = new { name = "Mohist", path = temp.Path },
                model = new { @default = "openai/gpt-4o", stage = new { build = "anthropic/claude" } }
            }));

        Assert.Equal("success", result.Status);
        Assert.NotNull(executor.Request);
        Assert.Equal("anthropic/claude", executor.Request.Model);
        Assert.Contains("#7 Add sessions", executor.Request.Prompt);
        Assert.Contains("Track agent activity", executor.Request.Prompt);
        Assert.Contains("Output Contract: Build Task", executor.Request.Prompt);
    }

    [Fact]
    public async Task AgentAction_FakeExecutorFailure_FailsWithoutRealAgent()
    {
        using var temp = new TempDir();
        var executor = new FakeAgentExecutor(new AgentExecutionResult(2, Stderr: "agent failed"));
        var action = new AgentAction(executor);

        var result = await action.ExecuteAsync(SpecHelpers.Context(
            temp.Path,
            "task",
            "mohist/agent",
            new { stage = "build", task = "task-1" }));

        Assert.Equal("failure", result.Status);
        Assert.Equal(2, result.ExitCode);
        Assert.Contains("agent failed", result.Message);
    }

    [Fact]
    public async Task AgentAction_WithSession_ReportsPromptAndCompletion()
    {
        using var temp = new TempDir();
        var executor = new FakeAgentExecutor(new AgentExecutionResult(0, "done"));
        var telemetry = new RecordingTelemetrySink();
        var action = new AgentAction(executor, telemetry);
        var context = new ActionContext(
            "wr_project_1",
            "work-1",
            "task",
            "build",
            "Build task",
            "mohist/agent",
            null,
            null,
            temp.Path,
            CancellationToken.None,
            new AgentSessionContext("session-1", "project", 1, "wr_project_1", "work-1", "build", "Build task"));

        var result = await action.ExecuteAsync(context);

        Assert.Equal("success", result.Status);
        Assert.Contains(telemetry.Events, e => e.Type == "mohist_prompt");
        Assert.Equal("completed", telemetry.Completed?.Status);
    }

    [Fact]
    public async Task AgentAction_MissingStage_FailsBeforeExecutor()
    {
        using var temp = new TempDir();
        var executor = new FakeAgentExecutor(new AgentExecutionResult(0));
        var action = new AgentAction(executor);

        var result = await action.ExecuteAsync(new ActionContext(
            "wr-1",
            "work-1",
            "task",
            "",
            "Work",
            "mohist/agent",
            null,
            null,
            temp.Path,
            CancellationToken.None));

        Assert.Equal("failure", result.Status);
        Assert.Null(executor.Request);
    }

    [Fact]
    public async Task AiReviewAction_WithFakeExecutor_WritesReviewRequest()
    {
        using var temp = new TempDir();
        var executor = new FakeAgentExecutor(new AgentExecutionResult(0, "reviewed"));
        var action = new AiReviewAction(executor);

        var result = await action.ExecuteAsync(SpecHelpers.Context(
            temp.Path,
            "task",
            "mohist/check/ai-review",
            new { changeDir = "openspec/changes/1-test" }));

        Assert.Equal("success", result.Status);
        Assert.NotNull(executor.Request);
        Assert.Equal("check", executor.Request.Stage);
        Assert.Equal("ai-review", executor.Request.Task);
        Assert.Contains("review.md", executor.Request.Prompt);
        Assert.EndsWith(Path.Combine("openspec", "changes", "1-test"), executor.Request.ChangeDir);
    }

    private sealed class FakeAgentExecutor : IAgentExecutor
    {
        private readonly AgentExecutionResult _result;

        public FakeAgentExecutor(AgentExecutionResult result)
        {
            _result = result;
        }

        public AgentExecutionRequest? Request { get; private set; }

        public Task<AgentExecutionResult> ExecuteAsync(AgentExecutionRequest request)
        {
            Request = request;
            return Task.FromResult(_result);
        }
    }

    private sealed class RecordingTelemetrySink : ISessionTelemetrySink
    {
        public List<SessionEventInput> Events { get; } = [];
        public SessionCompleted? Completed { get; private set; }

        public Task StartedAsync(AgentSessionContext session, SessionStarted started, CancellationToken ct) => Task.CompletedTask;

        public Task AppendAsync(AgentSessionContext session, IReadOnlyList<SessionEventInput> events, CancellationToken ct)
        {
            Events.AddRange(events);
            return Task.CompletedTask;
        }

        public Task CompletedAsync(AgentSessionContext session, SessionCompleted completed, CancellationToken ct)
        {
            Completed = completed;
            return Task.CompletedTask;
        }
    }
}
