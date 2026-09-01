using System.Text.Json;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;

namespace Mohist.Server.L0Tests.Workflow.Api;

public class WorkflowEventSerializationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void SystemTextJson_ShowsDefaultWorkflowEventUnionShape()
    {
        WorkflowEvent e = new TaskCompleted("build", "task-1");

        var json = JsonSerializer.Serialize(e, JsonOptions);

        Assert.Equal("""{"value":{"stage":"build","taskId":"task-1"}}""", json);
        Assert.Contains("build", json);
        Assert.Contains("task-1", json);
    }

    [Fact]
    public void SystemTextJson_RoundTripsWorkflowEventUnion()
    {
        WorkflowEvent e = new TaskCompleted("build", "task-1");

        var json = JsonSerializer.Serialize(e, JsonOptions);
        JsonSerializer.Deserialize<WorkflowEvent>(json);
    }
}
