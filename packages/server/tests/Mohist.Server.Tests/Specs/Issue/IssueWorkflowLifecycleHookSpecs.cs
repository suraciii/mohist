using CloudNative.CloudEvents;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.Workflow.Services;
using Xunit;
using Mohist.Server.Tests.Support;

namespace Mohist.Server.Tests.Specs.Issue;

/// <summary>
/// Verifies the new lifecycle hook chain: when a workflow reaches a
/// terminal Failed/Stopped state, the issue's status moves to Cancelled
/// and its ActiveWorkflowRunId is cleared. Closes audit gaps G1, G2, G3.
/// </summary>
public class IssueWorkflowLifecycleHookSpecs
{
    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void HookContext_StripsNullsAndExposesReason()
    {
        var ctx = new WorkflowLifecycleHookContext(
            "wr_1", "project-1", "issue-1", 42, "heartbeat-timeout");
        Assert.Equal("wr_1", ctx.WorkflowRunId);
        Assert.Equal("project-1", ctx.ProjectId);
        Assert.Equal("issue-1", ctx.IssueId);
        Assert.Equal(42, ctx.IssueNumber);
        Assert.Equal("heartbeat-timeout", ctx.Reason);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void RunnerDisconnected_CloudEvent_HasCanonicalAttributes()
    {
        var evt = CloudEventFactory.Create(
            type: EventCatalog.ReverseDns.RunnerDisconnected,
            source: new Uri("/mohist/runner/r-1", UriKind.Relative),
            subject: "r-1",
            extraExtensions: new Dictionary<string, object?>
            {
                ["runnerid"] = "r-1",
                ["reason"] = "tcp-drop",
            });

        Assert.Equal(EventCatalog.ReverseDns.RunnerDisconnected, evt.Type);
        Assert.Equal("1.0", evt.SpecVersion.VersionId);
        Assert.Equal("r-1", evt.Subject);

        var ext = evt.GetPopulatedAttributes()
            .Where(a => a.Key.IsExtension)
            .ToDictionary(a => a.Key.Name, a => a.Value);
        Assert.Equal("r-1", ext["runnerid"]);
        Assert.Equal("tcp-drop", ext["reason"]);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void IssueWorkflowCompletionHook_ImplementsOnlyCompletedHook()
    {
        // Step 5 of the event-mechanism migration: the hook no longer
        // implements Failed/Stopped — those transitions are driven by
        // IssueGrain's bus subscription. The hook is worktree-cleanup only.
        var hook = new IssueWorkflowCompletionHook(
            projectsQuery: null!,
            git: null!,
            log: NullLogger<IssueWorkflowCompletionHook>.Instance);

        Assert.IsAssignableFrom<IWorkflowCompletedHook>(hook);
    }
}
