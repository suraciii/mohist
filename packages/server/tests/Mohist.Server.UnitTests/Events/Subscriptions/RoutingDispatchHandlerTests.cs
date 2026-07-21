using System.Text.Json;
using Mohist.Server.Events.Subscriptions;
using Mohist.Server.Infrastructure.Events;
using Xunit;

namespace Mohist.Server.UnitTests.Events.Subscriptions;

public sealed class RoutingDispatchHandlerTests
{
    [Fact]
    public void PreflightLineage_UsesValidatedRunLineage_WhenExplicitRunEventOmitsIssueAndEpic()
    {
        var evt = new CloudEvent(
            "evt-1",
            new Uri("/mohist/workflow/run-1", UriKind.Relative),
            "com.mohist.workflow.run.failed",
            DateTimeOffset.Parse("2026-07-21T00:00:00Z"),
            JsonSerializer.SerializeToElement(new { }),
            extensions: new Dictionary<string, string>
            {
                [EventCatalog.Lineage.ProjectId] = "proj-1",
                [EventCatalog.Lineage.WorkflowRunId] = "run-1",
            });
        var unresolved = RoutedExecutionContextResolution.Unresolved(
            RoutedResolutionFailure.WorkspaceEmpty,
            "workflow run 'run-1' has no persisted workspace path",
            issueNumber: 42,
            epicNumber: 7);

        var lineage = RoutingDispatchHandler.PreflightLineage(evt, unresolved);

        Assert.Equal(42, lineage.IssueNumber);
        Assert.Equal(7, lineage.EpicNumber);
    }
}
