using System.Text.Json;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Tests.Specs.Workflow;
using Mohist.Server.Tests.Support;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services.Prompts;
using Xunit;

namespace Mohist.Server.Tests.Specs.Workflow.Grain;

[Collection("WorkflowGrain")]
public class TaskOutputVariablesEndToEndSpecs : WorkflowGrainSpecs
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public TaskOutputVariablesEndToEndSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task EndToEnd_TaskOutput_CapturedAndResolvedInDownstreamTask()
    {
        var definition = new WorkflowDefinition("spec/workflow",
        [
            new StageDefinition("build",
            [
                new("proposal", "Generate proposal", "spec/task",
                    Outputs:
                    [
                        new TaskOutputDefinition("openspecName", "output.openspecName"),
                        new TaskOutputDefinition("changeDir", "output.changeDir")
                    ]),
                new("specs", "Write specs", "spec/task",
                    With: new Dictionary<string, JsonElement?>
                    {
                        ["path"] = JsonSerializer.SerializeToElement("${{ tasks.proposal.outputs.openspecName }}/specs")
                    })
            ],
            [])
        ]);

        await StartWorkflowAsync(definition);

        var (proposal, runnerId) = await PollWorkAnyAsync();
        Assert.StartsWith("proposal.", proposal.WorkId);
        Assert.NotNull(proposal.Outputs);
        Assert.Contains("openspecName", proposal.Outputs);

        await ReportAsync(runnerId, proposal.WorkId, new WorkResult(
            "completed",
            CapturedOutputs: new Dictionary<string, JsonElement>
            {
                ["openspecName"] = JsonSerializer.SerializeToElement("issue-97"),
                ["changeDir"] = JsonSerializer.SerializeToElement("openspec/changes/issue-97")
            }));

        var (specs, _) = await PollWorkAnyAsync();
        Assert.StartsWith("specs.", specs.WorkId);
        Assert.NotNull(specs.With);
        Assert.NotNull(specs.Variables);

        var with = JsonSerializer.Deserialize<Dictionary<string, JsonElement?>>(specs.With, JsonOptions);
        Assert.NotNull(with);
        Assert.True(with.TryGetValue("path", out var pathEl) && pathEl.HasValue);
        var pathTemplate = pathEl!.Value.GetString();

        var variables = JsonSerializer.Deserialize<JsonElement>(specs.Variables, JsonOptions);
        var engine = new PromptTemplateEngine();
        var (rendered, missing, _) = engine.Render(pathTemplate!, variables);

        Assert.Empty(missing);
        Assert.Equal("issue-97/specs", rendered);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task EndToEnd_FailedTask_ProducesNoOutputsForDownstreamTask()
    {
        var definition = new WorkflowDefinition("spec/workflow",
        [
            new StageDefinition("build",
            [
                new("proposal", "Generate proposal", "spec/task",
                    Outputs:
                    [
                        new TaskOutputDefinition("openspecName", "output.openspecName")
                    ]),
                new("specs", "Write specs", "spec/task",
                    With: new Dictionary<string, JsonElement?>
                    {
                        ["path"] = JsonSerializer.SerializeToElement("${{ tasks.proposal.outputs.openspecName }}/specs")
                    })
            ],
            [])
        ]);

        await StartWorkflowAsync(definition);

        var (proposal, runnerId) = await PollWorkAnyAsync();
        Assert.StartsWith("proposal.", proposal.WorkId);

        await ReportAsync(runnerId, proposal.WorkId, new WorkResult(
            "failed",
            Message: "proposal generation failed",
            CapturedOutputs: new Dictionary<string, JsonElement>
            {
                ["openspecName"] = JsonSerializer.SerializeToElement("should-be-ignored")
            }));

        var workflow = Grains.GetGrain<IWorkflowGrain>(_workflowId!);
        Assert.True(await workflow.IsStoppedOrTerminalAsync());
        Assert.Equal("Failed", await workflow.GetRunStatusAsync());

        var downstream = await TryPollWorkAsync(runnerId, TimeSpan.FromMilliseconds(500));
        Assert.Null(downstream);
    }

    private async Task<WorkDispatch?> TryPollWorkAsync(string runnerId, TimeSpan timeout)
    {
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var work = await runner.PollAsync();
            if (work is not null) return work;
            await Task.Delay(50);
        }

        return null;
    }
}
