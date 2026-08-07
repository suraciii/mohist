using Mohist.Server.Infrastructure;
using System.Text.Json;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.TestSupport;
using Xunit;
using Mohist.Server.SpecTests.Specs.Workflow;

namespace Mohist.Server.SpecTests.Specs.Workflow.Grain;

[Collection("WorkflowExecution")]
public class RuntimeVariableDispatchSpecs : WorkflowGrainSpecs
{
    public RuntimeVariableDispatchSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Dispatch_AfterTaskOutputs_IncludesTaskOutputsInVariables()
    {
        await StartWorkflowAsync(new WorkflowDefinition(
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
            Output: JSON.DeserializeElement("{\"openspecName\":\"issue-97\",\"changeDir\":\"openspec/changes/issue-97\"}")));

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
    public async Task Dispatch_AfterCoreProcessOutput_ExposesTypedTaskOutputFields()
    {
        await StartWorkflowAsync(new WorkflowDefinition(
        [
            new StageDefinition("build",
            [
                new("process", "Run process", "core/process"),
                new("consume", "Consume process output", "spec/task")
            ],
            [])
        ]));

        var (process, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, process.WorkId, new WorkResult(
            "completed",
            Output: JSON.DeserializeElement("""{"stdout":"artifact.zip","exitCode":0}""")));

        var (consume, _) = await PollWorkAnyAsync();
        Assert.NotNull(consume.Variables);
        var variables = JsonSerializer.Deserialize<JsonElement>(consume.Variables);
        var output = variables.GetProperty("tasks").GetProperty("process").GetProperty("outputs");
        Assert.Equal("artifact.zip", output.GetProperty("stdout").GetString());
        Assert.Equal(JsonValueKind.Number, output.GetProperty("exitCode").ValueKind);
        Assert.Equal(0, output.GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public async Task Dispatch_RuntimeVariablesTakePrecedenceOverLowerPrecedenceSources()
    {
        await StartWorkflowAsync(new WorkflowDefinition(
        [
            new StageDefinition("build",
            [
                new("proposal", "Generate proposal", "spec/task"),
                new("specs", "Write specs", "spec/task")
            ],
            [])
        ]));

        var (proposal, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, proposal.WorkId, new WorkResult(
            "completed",
            Output: JSON.DeserializeElement("{\"openspecName\":\"runtime-value\"}")));

        var (specs, _) = await PollWorkAnyAsync();
        Assert.NotNull(specs.Variables);
        var variables = JsonSerializer.Deserialize<JsonElement>(specs.Variables);
        var openspecName = variables.GetProperty("tasks").GetProperty("proposal").GetProperty("outputs").GetProperty("openspecName");
        Assert.Equal("runtime-value", openspecName.GetString());
    }

    [Fact]
    public async Task Dispatch_EmptyTaskOutput_DoesNotAlterVariables()
    {
        await StartWorkflowAsync(new WorkflowDefinition(
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
