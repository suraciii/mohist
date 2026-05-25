using System.Text.Json;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.Tests.Specs;

public class WorkflowVariableSpecs : WorkflowGrainSpecs
{
    public WorkflowVariableSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task WorkflowDispatchKeepsTemplates()
    {
        await StartWorkflowAsync(new WorkflowDefinitionInput(
        [
            new StageDefinitionInput("build",
                [new("task-1", "Task 1", "spec/task", """
                { "path": "${{ artifacts.changeDir }}/proposal.md" }
                """)],
                [])
        ]));

        var (work, _) = await PollWorkAnyAsync();

        Assert.Contains("${{ artifacts.changeDir }}", work.With);
    }

    [Fact]
    public async Task WorkflowDispatchPreservesOpaqueContextAndAddsRuntimeContext()
    {
        await ClearBacklogAsync();
        _runnerId = await RegisterRunnerAsync();
        var workflowId = $"wr_{Guid.NewGuid():N}";
        _workflowId = workflowId;
        var workflow = Grains.GetGrain<IWorkflowGrain>(workflowId);
        var variables = JsonSerializer.Serialize(new Dictionary<string, JsonElement?>
        {
            ["custom"] = JsonSerializer.SerializeToElement(new { answer = 42 }),
            ["vars"] = JsonSerializer.SerializeToElement(new Dictionary<string, string>()),
        });
        await workflow.StartAsync(
            new WorkflowDefinitionInput([
                new StageDefinitionInput("build",
                    [new("task-1", "Task 1", "spec/task")],
                    [])
            ]),
            input: new WorkflowStartInput(variables));

        var (work, _) = await PollWorkAnyAsync();

        Assert.NotNull(work.Variables);
        using var document = JsonDocument.Parse(work.Variables);
        Assert.Equal(_workflowId, document.RootElement.GetProperty("workflow").GetProperty("runId").GetString());
        Assert.Equal("build", document.RootElement.GetProperty("stage").GetProperty("name").GetString());
        Assert.Equal(work.WorkId, document.RootElement.GetProperty("work").GetProperty("id").GetString());
        Assert.Equal("task", document.RootElement.GetProperty("work").GetProperty("type").GetString());
        Assert.Equal(42, document.RootElement.GetProperty("custom").GetProperty("answer").GetInt32());
        Assert.False(document.RootElement.TryGetProperty("issue", out _));
        Assert.False(document.RootElement.TryGetProperty("project", out _));
        Assert.False(document.RootElement.TryGetProperty("artifacts", out _));
        Assert.True(document.RootElement.TryGetProperty("vars", out _));
        Assert.False(document.RootElement.GetProperty("vars").TryGetProperty("planHealthCommand", out _));
    }

    [Fact]
    public async Task GenericWorkflowCorrelationDoesNotCreateIssueDispatchReference()
    {
        await ClearBacklogAsync();
        _runnerId = await RegisterRunnerAsync();
        var workflowId = $"wr_{Guid.NewGuid():N}";
        _workflowId = workflowId;
        var workflow = Grains.GetGrain<IWorkflowGrain>(workflowId);

        await workflow.StartAsync(
            new WorkflowDefinitionInput([
                new StageDefinitionInput("release",
                    [new("publish", "Publish", "spec/task")],
                    [])
            ]),
            new WorkflowCorrelationContext("project-1", "deployment", "deploy-1", null));

        var (work, _) = await PollWorkAnyAsync();

        Assert.Null(work.Issue);
        Assert.Equal("release", work.Stage);
    }

    [Fact]
    public async Task MohistPipelineUsesExpressionInputs()
    {
        await StartWorkflowAsync(Mohist.Server.Issue.Domain.MohistPipeline.Definition);

        var (proposal, r1) = await PollWorkAnyAsync();
        Assert.Contains("${{ artifacts.changeDir }}", proposal.With);
        await ReportAsync(r1, proposal.WorkId, "completed");

        var (specs, r2) = await PollWorkAnyAsync();
        await ReportAsync(r2, specs.WorkId, "completed");

        var (design, r3) = await PollWorkAnyAsync();
        await ReportAsync(r3, design.WorkId, "completed");

        var (tasks, r4) = await PollWorkAnyAsync();
        await ReportAsync(r4, tasks.WorkId, "completed");

        var (selfReview, r5) = await PollWorkAnyAsync();
        await ReportAsync(r5, selfReview.WorkId, "completed");

        var (check, _) = await PollWorkAnyAsync();
        Assert.Equal("checks", check.WorkType);
        Assert.StartsWith("checks-", check.WorkId);
    }

    [Fact]
    public async Task MohistPipelineUsesCoreActionsForGenericChecks()
    {
        await StartWorkflowAsync(Mohist.Server.Issue.Domain.MohistPipeline.Definition);

        for (var i = 0; i < 5; i++)
        {
            var (task, runnerId) = await PollWorkAnyAsync();
            await ReportAsync(runnerId, task.WorkId, "completed");
        }

        var (check, _) = await PollWorkAnyAsync();

        Assert.Equal("checks", check.WorkType);
        Assert.Contains("core/artifact-exists", check.With);
        Assert.Contains("core/marker", check.With);
        Assert.Contains("core/health-gate", check.With);
    }

    [Fact]
    public async Task MohistPipelineDispatchesAgentWorkWithoutExecutingAgent()
    {
        await StartWorkflowAsync(Mohist.Server.Issue.Domain.MohistPipeline.Definition);

        var (proposal, _) = await PollWorkAnyAsync();

        Assert.Equal("task", proposal.WorkType);
        Assert.Equal("plan", proposal.Stage);
        Assert.Equal("mohist/agent", proposal.Uses);
        Assert.Contains("proposal", proposal.WorkId);
        Assert.Contains("\"stage\":\"plan\"", proposal.With);
        Assert.Contains("\"task\":\"proposal\"", proposal.With);
    }
}
