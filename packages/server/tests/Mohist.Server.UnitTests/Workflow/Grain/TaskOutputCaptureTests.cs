using System.Text.Json;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.Grain;

public class TaskOutputCaptureTests
{
    [Fact]
    public void TaskRun_Output_StoresActionOutputAsJsonElement()
    {
        var task = MakeTaskRun("proposal.1", "proposal");

        task.Output = JsonSerializer.Deserialize<JsonElement>("{\"prNumber\":42,\"prUrl\":\"https://github.com/test\"}");

        Assert.True(task.Output.HasValue);
        var output = task.Output.Value;
        Assert.Equal(42, output.GetProperty("prNumber").GetInt32());
        Assert.Equal("https://github.com/test", output.GetProperty("prUrl").GetString());
    }

    [Fact]
    public void TaskRun_Output_NullWhenNoOutput()
    {
        var task = MakeTaskRun("proposal.1", "proposal");

        Assert.False(task.Output.HasValue);
        Assert.Equal(default, task.Output);
    }

    [Fact]
    public void TaskRun_Output_HandlesNonObjectOutput()
    {
        var task = MakeTaskRun("proposal.1", "proposal");

        task.Output = JsonSerializer.Deserialize<JsonElement>("\"plain text output\"");

        Assert.True(task.Output.HasValue);
        Assert.Equal(JsonValueKind.String, task.Output.Value.ValueKind);
        Assert.Equal("plain text output", task.Output.Value.GetString());
    }

    [Fact]
    public void TaskRun_Output_OverwritesOnRetry()
    {
        var task = MakeTaskRun("proposal.1", "proposal");

        task.Output = JsonSerializer.Deserialize<JsonElement>("{\"name\":\"first\"}");
        Assert.Equal("first", task.Output!.Value.GetProperty("name").GetString());

        task.Output = JsonSerializer.Deserialize<JsonElement>("{\"name\":\"second\"}");
        Assert.Equal("second", task.Output!.Value.GetProperty("name").GetString());
    }

    [Fact]
    public void TaskRun_Output_ArrayOutput()
    {
        var task = MakeTaskRun("proposal.1", "proposal");

        task.Output = JsonSerializer.Deserialize<JsonElement>("[{\"name\":\"item1\"},{\"name\":\"item2\"}]");

        Assert.True(task.Output.HasValue);
        Assert.Equal(JsonValueKind.Array, task.Output.Value.ValueKind);
        Assert.Equal(2, task.Output.Value.GetArrayLength());
    }

    private static TaskRun MakeTaskRun(string id, string definitionId)
    {
        return new TaskRun
        {
            Id = id,
            DefinitionId = definitionId,
            Attempt = 1,
            Title = "Task",
            Status = TaskRunStatus.Running
        };
    }
}
