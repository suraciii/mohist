using System.Text.Json;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;
using Mohist.Server.SpecTests.Support;

namespace Mohist.Server.SpecTests.Specs.Workflow.Api;

public class WorkflowEventSerializationSpecs
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void SystemTextJson_ShowsDefaultWorkflowEventUnionShape()
    {
        WorkflowEvent e = new TaskCompleted("build", "task-1");

        var json = JsonSerializer.Serialize(e, JsonOptions);

        Assert.Equal("""{"value":{"stage":"build","taskId":"task-1"}}""", json);
        Assert.Contains("build", json);
        Assert.Contains("task-1", json);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void SystemTextJson_RoundTripsWorkflowEventUnion()
    {
        WorkflowEvent e = new TaskCompleted("build", "task-1");

        var json = JsonSerializer.Serialize(e, JsonOptions);
        JsonSerializer.Deserialize<WorkflowEvent>(json);
    }
}
