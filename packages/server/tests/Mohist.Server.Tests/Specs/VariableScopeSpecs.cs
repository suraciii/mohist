using System.Text.Json;
using Mohist.Server.Issue.Grains;
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
    public async Task WorkflowDispatchIncludesExecutionAndDispatchContexts()
    {
        await ClearBacklogAsync();
        _runnerId = await RegisterRunnerAsync();
        var workflowId = $"wr_{Guid.NewGuid():N}";
        _workflowId = workflowId;
        var workflow = Grains.GetGrain<IWorkflowGrain>(workflowId);
        await workflow.StartAsync(
            new WorkflowDefinitionInput([
                new StageDefinitionInput("build",
                    [new("task-1", "Task 1", "spec/task")],
                    [])
            ]),
            new WorkflowIssueContext("project-1", "issue-1", 42, "Mohist", "/tmp/mohist", "main"),
            new WorkflowStartInput(new WorkflowIssueSeed("Add search", "Body", "openai/gpt-4o", new Dictionary<string, string> { ["build"] = "anthropic/claude" })));

        var (work, _) = await PollWorkAnyAsync();

        Assert.NotNull(work.Variables);
        using var document = JsonDocument.Parse(work.Variables);
        Assert.Equal(_workflowId, document.RootElement.GetProperty("workflow").GetProperty("runId").GetString());
        Assert.Equal("build", document.RootElement.GetProperty("stage").GetProperty("name").GetString());
        Assert.Equal(work.WorkId, document.RootElement.GetProperty("work").GetProperty("id").GetString());
        Assert.Equal("task", document.RootElement.GetProperty("work").GetProperty("type").GetString());
        Assert.Equal(42, document.RootElement.GetProperty("issue").GetProperty("number").GetInt32());
        Assert.Equal("Mohist", document.RootElement.GetProperty("project").GetProperty("name").GetString());
        Assert.Equal("openspec/changes/42-add-search", document.RootElement.GetProperty("artifacts").GetProperty("changeDir").GetString());
        Assert.True(document.RootElement.TryGetProperty("vars", out _));
        Assert.False(document.RootElement.GetProperty("vars").TryGetProperty("planHealthCommand", out _));
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
