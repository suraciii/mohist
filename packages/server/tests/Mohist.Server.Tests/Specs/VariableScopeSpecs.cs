using System.Text.Json;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.Tests.Specs;

public class WorkflowVariableSpecs : WorkflowGrainSpecs
{
    public WorkflowVariableSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task WorkflowDispatchKeepsTemplates()
    {
        await StartWorkflowAsync(new WorkflowDefinition("spec/workflow",
        [
            new StageDefinition("build",
                [new("task-1", "Task 1", "spec/task", With("""
                { "path": "${{ artifacts.changeDir }}/proposal.md" }
                """))],
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
            new WorkflowDefinition("spec/workflow", [
                new StageDefinition("build",
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
            new WorkflowDefinition("spec/workflow", [
                new StageDefinition("release",
                    [new("publish", "Publish", "spec/task")],
                    [])
            ]),
            new WorkflowCorrelationContext("project-1", "deployment", "deploy-1", null));

        var (work, _) = await PollWorkAnyAsync();

        Assert.Null(work.Issue);
        Assert.Equal("release", work.Stage);
    }

    [Fact]
    public async Task MohistWorkflowUsesExpressionInputs()
    {
        await StartWorkflowAsync(Mohist.Server.Issue.WorkflowProfiles.MohistWorkflow.Definition);

        var (proposal, r1) = await PollWorkAnyAsync();
        Assert.DoesNotContain("changeDir", proposal.With);
        Assert.NotNull(proposal.With);
        using (var proposalWith = JsonDocument.Parse(proposal.With))
        {
            Assert.False(proposalWith.RootElement.TryGetProperty("changeDir", out _));
            Assert.False(proposalWith.RootElement.TryGetProperty("openspecChangeDir", out _));
            Assert.Contains("proposal.md", proposalWith.RootElement.GetProperty("prompt").GetString());
            Assert.Equal("${{ openspecChangeDir }}/proposal.md", proposalWith.RootElement.GetProperty("expect").GetProperty("files")[0].GetProperty("path").GetString());
            Assert.False(proposalWith.RootElement.GetProperty("expect").TryGetProperty("markers", out _));
        }
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
        Assert.Contains("${{ openspecChangeDir }}", check.With);
        Assert.DoesNotContain("${{ artifacts.changeDir }}", check.With);
    }

    [Fact]
    public async Task MohistWorkflowUsesCoreActionsForGenericChecks()
    {
        await StartWorkflowAsync(Mohist.Server.Issue.WorkflowProfiles.MohistWorkflow.Definition);

        for (var i = 0; i < 5; i++)
        {
            var (task, runnerId) = await PollWorkAnyAsync();
            await ReportAsync(runnerId, task.WorkId, "completed");
        }

        var (check, _) = await PollWorkAnyAsync();

        Assert.Equal("checks", check.WorkType);
        Assert.Contains("core/artifact-exists", check.With);
        Assert.Contains("core/marker", check.With);
        Assert.Contains("core/script", check.With);
        Assert.Contains("\"name\":\"health\"", check.With);
        Assert.Contains("\"run\":\"git diff --check\"", check.With);
    }

    [Fact]
    public async Task MohistWorkflowDispatchesAgentWorkWithoutExecutingAgent()
    {
        await StartWorkflowAsync(Mohist.Server.Issue.WorkflowProfiles.MohistWorkflow.Definition);

        var (proposal, _) = await PollWorkAnyAsync();

        Assert.Equal("task", proposal.WorkType);
        Assert.Equal("plan", proposal.Stage);
        Assert.Equal("mohist/acp-agent", proposal.Uses);
        Assert.Contains("proposal", proposal.WorkId);
        Assert.Contains("\"prompt\"", proposal.With);
        Assert.DoesNotContain("\"stage\":", proposal.With);
        Assert.DoesNotContain("\"task\":", proposal.With);
        Assert.DoesNotContain("changeDir", proposal.With);
        Assert.Contains("\"expect\"", proposal.With);
        Assert.Contains("${{ openspecChangeDir }}/proposal.md", proposal.With);
    }
}
