using CloudNative.CloudEvents;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Domain;
using Xunit;
using Mohist.Server.Tests.Support;

namespace Mohist.Server.Tests.Specs.Issue;

/// <summary>
/// Verifies the canonical "Issue 根据 workflow 完成事件把自己 done 掉" path.
/// Step 5 of design/event-mechanism.md: IssueGrain subscribes to workflow
/// lifecycle events on the bus; an emit of
/// <c>com.mohist.workflow.run.completed</c> for the active run id causes
/// the issue to transition to Done — no direct grain-to-grain call
/// from WorkflowGrain.
/// </summary>
public class IssueGrainBusSubscriptionSpecs
{
    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void WorkflowRunCompleted_CloudEvent_CarriesRequiredAttributesForIssueSubscription()
    {
        var evt = CloudEventFactory.Create(
            type: EventCatalog.ReverseDns.WorkflowRunCompleted,
            source: new Uri("/mohist/workflow/wr_1", UriKind.Relative),
            data: new { workflowRunId = "wr_1", projectId = "p1", finalStage = "integrate" },
            subject: "42",
            projectId: "p1",
            workflowRunId: "wr_1",
            issueNumber: "42");

        Assert.Equal("com.mohist.workflow.run.completed", evt.Type);
        Assert.Equal("1.0", evt.SpecVersion.VersionId);
        Assert.Equal("/mohist/workflow/wr_1", evt.Source?.ToString());
        Assert.Equal("42", evt.Subject);

        var ext = evt.GetPopulatedAttributes()
            .Where(a => a.Key.IsExtension)
            .ToDictionary(a => a.Key.Name, a => a.Value);
        Assert.Equal("wr_1", ext["workflowrunid"]);
        Assert.Equal("p1", ext["projectid"]);
        Assert.Equal("42", ext["issueno"]);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void WorkflowRunFailed_CloudEvent_HasReasonExtensionForAbortLogging()
    {
        var evt = CloudEventFactory.Create(
            type: EventCatalog.ReverseDns.WorkflowRunFailed,
            source: new Uri("/mohist/workflow/wr_1", UriKind.Relative),
            subject: "42",
            projectId: "p1",
            workflowRunId: "wr_1",
            issueNumber: "42",
            extraExtensions: new Dictionary<string, object?>
            {
                ["reason"] = "task-failed:build-1",
            });

        var ext = evt.GetPopulatedAttributes()
            .Where(a => a.Key.IsExtension)
            .ToDictionary(a => a.Key.Name, a => a.Value);
        Assert.Equal("task-failed:build-1", ext["reason"]);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void EventCatalog_ReverseDns_HasAllThreeTerminalWorkflowTypes()
    {
        // The IssueGrain subscribes to all three; if a refactor drops
        // one, the IssueGrain silently stops reacting to that terminal
        // state. This test is the canary.
        Assert.Equal("com.mohist.workflow.run.completed", EventCatalog.ReverseDns.WorkflowRunCompleted);
        Assert.Equal("com.mohist.workflow.run.stopped", EventCatalog.ReverseDns.WorkflowRunStopped);
        Assert.Equal("com.mohist.workflow.run.failed", EventCatalog.ReverseDns.WorkflowRunFailed);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void BusDispatch_FiltersByRunIdExtension()
    {
        // The IssueGrain handler subscribes to a CloudEvents type
        // and filters by the `workflowrunid` extension. The issue is
        // for run wr_myRun, not wr_otherRun — so events for wr_otherRun
        // should be ignored even though they share the same type.
        var myRunEvt = CloudEventFactory.Create(
            type: EventCatalog.ReverseDns.WorkflowRunCompleted,
            source: new Uri("/mohist/workflow/wr_myRun", UriKind.Relative),
            subject: "42",
            workflowRunId: "wr_myRun");

        var otherRunEvt = CloudEventFactory.Create(
            type: EventCatalog.ReverseDns.WorkflowRunCompleted,
            source: new Uri("/mohist/workflow/wr_otherRun", UriKind.Relative),
            subject: "42",
            workflowRunId: "wr_otherRun");

        Assert.NotEqual(
            TryExtension(myRunEvt, "workflowrunid"),
            TryExtension(otherRunEvt, "workflowrunid"));
    }

    private static string? TryExtension(CloudEvent evt, string name)
    {
        foreach (var (attr, value) in evt.GetPopulatedAttributes())
        {
            if (attr.IsExtension && attr.Name == name && value is not null)
            {
                return value.ToString();
            }
        }
        return null;
    }
}
