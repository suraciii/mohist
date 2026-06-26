using System.Text.Json;
using Mohist.Server.Events.Subscriptions;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Specs.Events.Workflow;

public class WorkflowStageLockReleaseHandlerSpecs
{
    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void HasSubscriptionAttributeWithExpectedType()
    {
        var attr = (SubscriptionAttribute?)Attribute.GetCustomAttribute(
            typeof(WorkflowStageLockReleaseHandler), typeof(SubscriptionAttribute));
        Assert.NotNull(attr);
        Assert.Equal(
            "com.mohist.workflow.stage.completed|com.mohist.workflow.stage.failed",
            attr!.Type);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Theory]
    [InlineData("/mohist/workflow-runs/wf_abc", "wf_abc")]
    [InlineData("https://example.com/mohist/workflow-runs/wf_xyz", "")]
    [InlineData("/mohist/issue/issue_1", "")]
    [InlineData("", "")]
    public void ExtractWorkflowRunId_ReturnsExpected(string source, string expected)
    {
        Assert.Equal(expected, WorkflowStageLockReleaseHandler.ExtractWorkflowRunId(source));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void ExtractStage_FromValueEnvelope_ReturnsInnerStage()
    {
        var data = JsonSerializer.SerializeToElement(
            new { value = new { stage = "integrate", reason = "x" } },
            CloudEvent.JsonOptions);

        Assert.Equal("integrate", WorkflowStageLockReleaseHandler.ExtractStage(data));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void ExtractStage_FromBareObject_FallsBackToTopLevelStage()
    {
        var data = JsonSerializer.SerializeToElement(
            new { stage = "build" },
            CloudEvent.JsonOptions);

        Assert.Equal("build", WorkflowStageLockReleaseHandler.ExtractStage(data));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void ExtractStage_AcceptsPascalCaseStageProperty()
    {
        var data = JsonSerializer.SerializeToElement(
            new { value = new { Stage = "release" } },
            CloudEvent.JsonOptions);

        Assert.Equal("release", WorkflowStageLockReleaseHandler.ExtractStage(data));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void ExtractStage_FromNullOrNonObject_ReturnsNull()
    {
        Assert.Null(WorkflowStageLockReleaseHandler.ExtractStage(null));
        Assert.Null(WorkflowStageLockReleaseHandler.ExtractStage(JsonSerializer.SerializeToElement("plain", CloudEvent.JsonOptions)));
        Assert.Null(WorkflowStageLockReleaseHandler.ExtractStage(JsonSerializer.SerializeToElement(42, CloudEvent.JsonOptions)));
    }
}
