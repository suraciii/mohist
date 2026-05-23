using System.Text.Json;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Variables.Grains;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.Tests.Specs;

public class VariableScopeSpecs : WorkflowGrainSpecs
{
    public VariableScopeSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

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
    public async Task VariableScopeAddsDispatchVariables()
    {
        var scope = Grains.GetGrain<IVariableScopeGrain>($"scope-{Guid.NewGuid():N}");
        await scope.SetContextAsync("issue", """{ "number": 42 }""");

        var snapshot = await scope.SnapshotAsync(new VariableSnapshotRequest("wr-1", "task-1.1", "task", "build", "Task 1"));

        using var document = JsonDocument.Parse(snapshot);
        Assert.Equal(42, document.RootElement.GetProperty("issue").GetProperty("number").GetInt32());
        Assert.Equal("wr-1", document.RootElement.GetProperty("workflow").GetProperty("runId").GetString());
        Assert.Equal("build", document.RootElement.GetProperty("stage").GetProperty("name").GetString());
        Assert.Equal("task-1.1", document.RootElement.GetProperty("work").GetProperty("id").GetString());
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
        Assert.Equal("mohist/artifact-exists", check.Uses);
        Assert.Contains("${{ artifacts.changeDir }}/proposal.md", check.With);
    }
}
