using System.Text.Json;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Tests.Support;
using Xunit;
using Mohist.Server.Tests.Specs.Workflow;

namespace Mohist.Server.Tests.Specs.Workflow.Grain;

public class RuntimeVariableDispatchSpecs : WorkflowGrainSpecs
{
    public RuntimeVariableDispatchSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task Dispatch_AfterTaskOutputs_IncludesRuntimeVariablesInResolvedVars()
    {
        await StartWorkflowAsync(new WorkflowDefinition("spec/workflow",
        [
            new StageDefinition("build",
            [
                new("proposal", "Generate proposal", "spec/task", Outputs:
                [
                    new TaskOutputDefinition("openspecName", "output.openspecName")
                ]),
                new("specs", "Write specs", "spec/task")
            ],
            [])
        ]));

        var (proposal, r1) = await PollWorkAnyAsync();
        Assert.StartsWith("proposal.", proposal.WorkId);

        await ReportAsync(r1, proposal.WorkId, new WorkResult(
            "completed",
            CapturedOutputs: new Dictionary<string, JsonElement>
            {
                ["openspecName"] = JsonSerializer.SerializeToElement("issue-97")
            }));

        var (specs, _) = await PollWorkAnyAsync();
        Assert.StartsWith("specs.", specs.WorkId);
        Assert.NotNull(specs.Variables);

        var variables = JsonSerializer.Deserialize<JsonElement>(specs.Variables);
        Assert.True(variables.TryGetProperty("tasks", out var tasks));
        Assert.True(tasks.TryGetProperty("proposal", out var proposalEl));
        Assert.True(proposalEl.TryGetProperty("outputs", out var outputs));
        Assert.True(outputs.TryGetProperty("openspecName", out var openspecName));
        Assert.Equal("issue-97", openspecName.GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task Dispatch_RuntimeVariablesTakePrecedenceOverLowerPrecedenceSources()
    {
        await StartWorkflowAsync(new WorkflowDefinition("spec/workflow",
        [
            new StageDefinition("build",
            [
                new("proposal", "Generate proposal", "spec/task", Outputs:
                [
                    new TaskOutputDefinition("openspecName", "output.openspecName")
                ]),
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
            CapturedOutputs: new Dictionary<string, JsonElement>
            {
                ["openspecName"] = JsonSerializer.SerializeToElement("runtime-value")
            }));

        var (specs, _) = await PollWorkAnyAsync();
        Assert.NotNull(specs.Variables);
        var variables = JsonSerializer.Deserialize<JsonElement>(specs.Variables);
        var openspecName = variables.GetProperty("tasks").GetProperty("proposal").GetProperty("outputs").GetProperty("openspecName");
        Assert.Equal("runtime-value", openspecName.GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task Dispatch_EmptyRuntimeStore_DoesNotAlterVariables()
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
