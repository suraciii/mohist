using System.Text.Json;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.SpecTests.Support;
using Xunit;
using Mohist.Server.SpecTests.Specs.Workflow;

namespace Mohist.Server.SpecTests.Specs.Workflow.Grain;

[Collection("WorkflowGrain2")]
public class RuntimeVariableDispatchSpecs : WorkflowGrainSpecs
{
    public RuntimeVariableDispatchSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Dispatch_AfterTaskOutputs_IncludesTaskOutputsInVariables()
    {
        await StartWorkflowAsync(new WorkflowDefinition("spec/workflow",
        [
            new StageDefinition("build",
            [
                new("proposal", "Generate proposal", "spec/task"),
                new("specs", "Write specs", "spec/task")
            ],
            [])
        ]));

        var (proposal, r1) = await PollWorkAnyAsync();
        Assert.StartsWith("proposal.", proposal.WorkId);

        await ReportAsync(r1, proposal.WorkId, new WorkResult(
            "completed",
            Output: "{\"openspecName\":\"issue-97\",\"changeDir\":\"openspec/changes/issue-97\"}"));

        var (specs, _) = await PollWorkAnyAsync();
        Assert.StartsWith("specs.", specs.WorkId);
        Assert.NotNull(specs.Variables);

        var variables = JsonSerializer.Deserialize<JsonElement>(specs.Variables);
        Assert.True(variables.TryGetProperty("tasks", out var tasks));
        Assert.True(tasks.TryGetProperty("proposal", out var proposalEl));
        Assert.True(proposalEl.TryGetProperty("outputs", out var outputs));
        Assert.True(outputs.TryGetProperty("openspecName", out var openspecName));
        Assert.Equal("issue-97", openspecName.GetString());
        Assert.True(outputs.TryGetProperty("changeDir", out var changeDir));
        Assert.Equal("openspec/changes/issue-97", changeDir.GetString());
    }

    [Fact]
    public async Task Dispatch_RuntimeVariablesTakePrecedenceOverLowerPrecedenceSources()
    {
        await StartWorkflowAsync(new WorkflowDefinition("spec/workflow",
        [
            new StageDefinition("build",
            [
                new("proposal", "Generate proposal", "spec/task"),
                new("specs", "Write specs", "spec/task")
            ],
            [],
            Variables: new Dictionary<string, JsonElement?>
            {
                ["tasks"] = JsonSerializer.SerializeToElement(new
                {
                    proposal = new
                    {
                        outputs = new
                        {
                            openspecName = "static-value"
                        }
                    }
                })
            })
        ]));

        var (proposal, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, proposal.WorkId, new WorkResult(
            "completed",
            Output: "{\"openspecName\":\"runtime-value\"}"));

        var (specs, _) = await PollWorkAnyAsync();
        Assert.NotNull(specs.Variables);
        var variables = JsonSerializer.Deserialize<JsonElement>(specs.Variables);
        var openspecName = variables.GetProperty("tasks").GetProperty("proposal").GetProperty("outputs").GetProperty("openspecName");
        Assert.Equal("runtime-value", openspecName.GetString());
    }

    [Fact]
    public async Task Dispatch_EmptyTaskOutput_DoesNotAlterVariables()
    {
        await StartWorkflowAsync(new WorkflowDefinition("spec/workflow",
        [
            new StageDefinition("build",
            [
                new("proposal", "Generate proposal", "spec/task"),
                new("specs", "Write specs", "spec/task")
            ],
            [])
        ]));

        var (proposal, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, proposal.WorkId, "completed");

        var (specs, _) = await PollWorkAnyAsync();
        Assert.NotNull(specs.Variables);
        var variables = JsonSerializer.Deserialize<JsonElement>(specs.Variables);
        Assert.False(variables.TryGetProperty("tasks", out _));
    }
}
