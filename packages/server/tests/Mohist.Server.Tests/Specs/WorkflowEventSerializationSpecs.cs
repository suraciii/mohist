using System.Text.Json;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;

namespace Mohist.Server.Tests.Specs;

public class WorkflowEventSerializationSpecs
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void SystemTextJson_ShowsDefaultWorkflowEventUnionShape()
    {
        WorkflowEvent e = new TaskCompleted("build", "task-1");

        var json = JsonSerializer.Serialize(e, JsonOptions);

        Assert.Equal("""{"stage":"build","taskId":"task-1"}""", json);
        Assert.Contains("build", json);
        Assert.Contains("task-1", json);
    }

    [Fact]
    public void SystemTextJson_CannotRoundTripWorkflowEventUnionWithoutType()
    {
        WorkflowEvent e = new TaskCompleted("build", "task-1");

        var json = JsonSerializer.Serialize(e, JsonOptions);

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<WorkflowEvent>(json));
    }
}
