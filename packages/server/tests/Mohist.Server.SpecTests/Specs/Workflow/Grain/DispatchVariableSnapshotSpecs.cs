using System.Text.Json;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Grain;

public class DispatchVariableSnapshotSpecs : WorkflowGrainSpecs
{
    public DispatchVariableSnapshotSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task RuntimeTaskWithPlaceholder_RetryAfterVariableChange_UsesNewValue()
    {
        var workflow = await StartWorkflowAsync(new WorkflowDefinition("spec/workflow",
        [
            new StageDefinition("build", [new("load-tasks", "Load tasks", "spec/load")], [new("check-1", "Check 1", "spec/check")],
                Variables: new Dictionary<string, JsonElement?>
                {
                    ["agent"] = JsonSerializer.SerializeToElement(new { type = "opencode", model = "model-a" })
                })
        ]));

        var (load, r1) = await PollWorkAnyAsync();
        await _fixture.Grains.GetGrain<IWorkflowGrain>(_workflowId!).AddTasksAsync(
            new AddTasksBatchRequest([
                new AddTasksBatchItem("T-001", "Implement feature", "mohist/opencode", JsonSerializer.Deserialize<JsonElement>("""
                    {"options":"${{ vars.agent }}"}
                    """))
            ]));
        await ReportAsync(r1, load.WorkId, "completed");

        var (dynamicTask, r2) = await PollWorkAnyAsync();
        Assert.StartsWith("T-001.1", dynamicTask.WorkId);
        Assert.Contains("${{ vars.agent }}", dynamicTask.With);
        Assert.DoesNotContain("model-a", dynamicTask.With);
        Assert.NotNull(dynamicTask.Variables);
        using (var firstVars = JsonDocument.Parse(dynamicTask.Variables!))
            Assert.Equal("model-a", firstVars.RootElement.GetProperty("vars").GetProperty("agent").GetProperty("model").GetString());

        await ReportAsync(r2, dynamicTask.WorkId, "failed", "expected flaky");
        await PatchIssueVariablesAsync(TestIssueNumber(_workflowId!), new VariableBundle(
            Stages: new Dictionary<string, StageVariables>
            {
                ["build"] = new(JsonSerializer.SerializeToElement(new { agent = new { type = "opencode", model = "model-b" } }))
            }));
        await workflow.RetryAsync();

        var (retriedTask, r3) = await PollWorkAnyAsync();
        Assert.StartsWith("T-001.2", retriedTask.WorkId);
        Assert.Contains("${{ vars.agent }}", retriedTask.With);
        Assert.DoesNotContain("model-a", retriedTask.With);
        Assert.DoesNotContain("model-b", retriedTask.With);
        Assert.NotNull(retriedTask.Variables);
        using (var retryVars = JsonDocument.Parse(retriedTask.Variables!))
            Assert.Equal("model-b", retryVars.RootElement.GetProperty("vars").GetProperty("agent").GetProperty("model").GetString());

        await ReportAsync(r3, retriedTask.WorkId, "completed");
        var (check, r4) = await PollWorkAnyAsync();
        await ReportChecksPassAsync(r4, check, "check-1");
    }

    [Fact]
    public async Task RuntimeTaskWithBakedLiteral_Retry_UsesBakedValue()
    {
        var workflow = await StartWorkflowAsync(new WorkflowDefinition("spec/workflow",
        [new StageDefinition("build", [new("load-tasks", "Load tasks", "spec/load")], [new("check-1", "Check 1", "spec/check")]) ]));

        var (load, r1) = await PollWorkAnyAsync();
        await _fixture.Grains.GetGrain<IWorkflowGrain>(_workflowId!).AddTasksAsync(
            new AddTasksBatchRequest([
                new AddTasksBatchItem("T-001", "Implement feature", "mohist/opencode", JsonSerializer.Deserialize<JsonElement>("""
                    {"options":{"type":"opencode","model":"model-a"}}
                    """))
            ]));
        await ReportAsync(r1, load.WorkId, "completed");

        var (dynamicTask, r2) = await PollWorkAnyAsync();
        Assert.StartsWith("T-001.1", dynamicTask.WorkId);
        Assert.Contains("model-a", dynamicTask.With);
        await ReportAsync(r2, dynamicTask.WorkId, "failed", "expected flaky");
        await workflow.RetryAsync();

        var (retriedTask, r3) = await PollWorkAnyAsync();
        Assert.StartsWith("T-001.2", retriedTask.WorkId);
        Assert.Contains("model-a", retriedTask.With);
        Assert.DoesNotContain("model-b", retriedTask.With);
        await ReportAsync(r3, retriedTask.WorkId, "completed");

        var (check, r4) = await PollWorkAnyAsync();
        await ReportChecksPassAsync(r4, check, "check-1");
    }
}
