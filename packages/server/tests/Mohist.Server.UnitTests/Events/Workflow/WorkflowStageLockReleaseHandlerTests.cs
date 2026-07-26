using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Workflow.Subscriptions;
using Xunit;

namespace Mohist.Server.UnitTests.Events.Workflow;

public class WorkflowStageLockReleaseHandlerTests
{
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

    [Fact]
    public void ReadWorkflowRunId_UsesCanonicalEnvelopeExtension()
    {
        var extensions = new Dictionary<string, string>
        {
            [EventCatalog.Lineage.WorkflowRunId] = "wf_abc",
        };

        Assert.Equal("wf_abc", CloudEventLineage.ReadValue(extensions, EventCatalog.Lineage.WorkflowRunId));
    }

    [Fact]
    public void ReadStage_UsesCanonicalEnvelopeExtension()
    {
        var extensions = new Dictionary<string, string>
        {
            [EventCatalog.Lineage.Stage] = "release",
        };

        Assert.Equal("release", CloudEventLineage.ReadValue(extensions, EventCatalog.Lineage.Stage));
    }
}
