using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Services;
using Mohist.Server.Events.Subscriptions;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;
using static Mohist.Server.SpecTests.Specs.Events.Subscriptions.AgentSubscriptionDispatchTestSupport;

namespace Mohist.Server.SpecTests.Specs.Events.Subscriptions;

/// <summary>
/// Unit specs for <see cref="AgentSubscriptionDispatchHandler"/>. The
/// handler is the dispatch pipeline's event-driven entry point
/// (issue-391 T-003). These specs cover:
/// <list type="bullet">
///   <item>Event-level arbitration: highest-priority Agent takes the
///         event, fallback/takeover, same-Agent multiple-match selection,
///         tie-break determinism, no-match no-op (spec
///         <c>agent-subscription-dispatch#Event-level arbitration</c>).</item>
///   <item>Lifecycle invariants: archived subscriptions do not trigger,
///         archived Agents do not trigger their subscriptions, running
///         sessions are unaffected (spec
///         <c>agent-subscription-management#Lifecycle invariant</c>).</item>
///   <item>Envelope-only boundary: the handler reads
///         <c>extensions["projectid"]</c> from the envelope (issue events
///         stamp it in <c>IssueStore.SaveAsync</c>; workflow
///         events stamp it in <c>WorkflowRunStore.ToCloudEvent</c> from the
///         run's metadata annotations) and skips when absent.</item>
/// </list>
/// </summary>
public class AgentSubscriptionDispatchHandlerSpecs
{
    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task HandleAsync_HighestPriorityAgentWins_OnlyOneLaunchFires()
    {
        var (recorder, scope) = Build();
        await using var _ = scope;
        await SeedProjectAsync(scope, "proj_a");
        await SeedAgentAsync(scope, "proj_a", "agent_high", "high-agent");
        await SeedAgentAsync(scope, "proj_a", "agent_low", "low-agent");

        await SeedSubscriptionAsync(scope, "proj_a", "agent_low",
            name: "low-fallback", filterType: "com.mohist.workflow.stage.*", priority: 0);
        await SeedSubscriptionAsync(scope, "proj_a", "agent_high",
            name: "high-takeover", filterType: "com.mohist.workflow.stage.*", priority: 100);

        var evt = BuildWorkflowEvent(
            type: "com.mohist.workflow.stage.approval-requested",
            workflowRunId: "wr_1");

        await scope.Handler.HandleAsync(evt, CancellationToken.None);

        var launch = Assert.Single(recorder.Calls);
        Assert.Equal("agent_high", launch.Agent.Id);
        Assert.Equal("high-takeover", launch.Prompt); // rendered prompt (no placeholders)
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task HandleAsync_FallbackAndTakeover_SourceConstraintPicksTakeoverForThatRunOnly()
    {
        var (recorder, scope) = Build();
        await using var _ = scope;
        await SeedProjectAsync(scope, "proj_a");
        await SeedAgentAsync(scope, "proj_a", "agent_fallback", "fallback-agent");
        await SeedAgentAsync(scope, "proj_a", "agent_takeover", "takeover-agent");

        // Global fallback on agent_fallback (low priority, no source
        // constraint so it matches every workflow run).
        await SeedSubscriptionAsync(scope, "proj_a", "agent_fallback",
            name: "fallback", filterType: "com.mohist.workflow.stage.*", priority: 0,
            source: null);

        // Takeover on agent_takeover (high priority, scoped to a specific run).
        await SeedSubscriptionAsync(scope, "proj_a", "agent_takeover",
            name: "takeover", filterType: "com.mohist.workflow.stage.*", priority: 100,
            source: "/mohist/workflow-runs/wr_target");

        // Event for the targeted run — takeover wins.
        await scope.Handler.HandleAsync(
            BuildWorkflowEvent(type: "com.mohist.workflow.stage.approval-requested",
                workflowRunId: "wr_target"),
            CancellationToken.None);

        Assert.Equal("takeover-agent", Assert.Single(recorder.Calls).Agent.Name);

        recorder.Calls.Clear();

        // Event for a different run — fallback wins.
        await scope.Handler.HandleAsync(
            BuildWorkflowEvent(type: "com.mohist.workflow.stage.approval-requested",
                workflowRunId: "wr_other"),
            CancellationToken.None);

        Assert.Equal("fallback-agent", Assert.Single(recorder.Calls).Agent.Name);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task HandleAsync_NoMatch_NoLaunchFires()
    {
        var (recorder, scope) = Build();
        await using var _ = scope;
        await SeedProjectAsync(scope, "proj_a");
        await SeedAgentAsync(scope, "proj_a", "agent_a", "agent-a");

        await SeedSubscriptionAsync(scope, "proj_a", "agent_a",
            name: "only-stage", filterType: "com.mohist.workflow.stage.*", priority: 5);

        // Event that doesn't match the subscription's type.
        var evt = BuildWorkflowEvent(
            type: "com.mohist.workflow.run.completed",
            workflowRunId: "wr_x");

        await scope.Handler.HandleAsync(evt, CancellationToken.None);

        Assert.Empty(recorder.Calls);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task HandleAsync_SameAgentMultipleMatches_OnlyHighestPriorityFires()
    {
        var (recorder, scope) = Build();
        await using var _ = scope;
        await SeedProjectAsync(scope, "proj_a");
        await SeedAgentAsync(scope, "proj_a", "agent_a", "agent-a");

        await SeedSubscriptionAsync(scope, "proj_a", "agent_a",
            name: "low", filterType: "com.mohist.workflow.stage.*", priority: 1,
            source: "/mohist/workflow-runs/wr_a");
        await SeedSubscriptionAsync(scope, "proj_a", "agent_a",
            name: "high", filterType: "com.mohist.workflow.stage.*", priority: 99,
            source: "/mohist/workflow-runs/wr_a");

        var evt = BuildWorkflowEvent(
            type: "com.mohist.workflow.stage.approval-requested",
            workflowRunId: "wr_a");

        await scope.Handler.HandleAsync(evt, CancellationToken.None);

        var launch = Assert.Single(recorder.Calls);
        Assert.Equal("agent_a", launch.Agent.Id);
        Assert.Equal("high", launch.Prompt); // subscription name = response prompt
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task HandleAsync_EqualPriorityAcrossAgents_DeterministicTieBreakBySubscriptionId()
    {
        var (recorder, scope) = Build();
        await using var _ = scope;
        await SeedProjectAsync(scope, "proj_a");
        await SeedAgentAsync(scope, "proj_a", "agent_b", "agent-b");
        await SeedAgentAsync(scope, "proj_a", "agent_a", "agent-a");

        // Both subscriptions have priority 50 (a tie). The deterministic
        // tie-break must select the lexicographically smallest subscription
        // id across the two group winners — regardless of AgentId.
        await SeedSubscriptionAsync(scope, "proj_a", "agent_a",
            id: "subs_zzz",
            filterType: "com.mohist.workflow.stage.*", priority: 50);
        await SeedSubscriptionAsync(scope, "proj_a", "agent_b",
            id: "subs_aaa",
            filterType: "com.mohist.workflow.stage.*", priority: 50);

        var evt = BuildWorkflowEvent(
            type: "com.mohist.workflow.stage.approval-requested",
            workflowRunId: "wr_1");

        await scope.Handler.HandleAsync(evt, CancellationToken.None);

        var launch = Assert.Single(recorder.Calls);
        // The lexicographically smaller subscription id "subs_aaa" wins.
        Assert.Equal("subs_aaa", launch.Prompt);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task HandleAsync_EqualPriorityWithinAgent_DeterministicTieBreakBySubscriptionId()
    {
        var (recorder, scope) = Build();
        await using var _ = scope;
        await SeedProjectAsync(scope, "proj_a");
        await SeedAgentAsync(scope, "proj_a", "agent_a", "agent-a");

        await SeedSubscriptionAsync(scope, "proj_a", "agent_a",
            id: "subs_zzz", name: "later-name",
            filterType: "com.mohist.workflow.stage.*", priority: 7);
        await SeedSubscriptionAsync(scope, "proj_a", "agent_a",
            id: "subs_aaa", name: "earlier-name",
            filterType: "com.mohist.workflow.stage.*", priority: 7);

        var evt = BuildWorkflowEvent(
            type: "com.mohist.workflow.stage.approval-requested",
            workflowRunId: "wr_1");

        await scope.Handler.HandleAsync(evt, CancellationToken.None);

        var launch = Assert.Single(recorder.Calls);
        // Tied priority, so the lexicographically smaller SubscriptionId wins.
        Assert.Equal("earlier-name", launch.Prompt);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task HandleAsync_ReproducibleForSameInputs()
    {
        // Determinism requirement: the same matched subscriptions for the
        // same event must always resolve to the same selection.
        var (recorder, scope) = Build();
        await using var _ = scope;
        await SeedProjectAsync(scope, "proj_a");
        await SeedAgentAsync(scope, "proj_a", "agent_x", "agent-x");
        await SeedAgentAsync(scope, "proj_a", "agent_y", "agent-y");

        await SeedSubscriptionAsync(scope, "proj_a", "agent_x",
            id: "subs_2", name: "x-sub",
            filterType: "com.mohist.workflow.stage.*", priority: 1);
        await SeedSubscriptionAsync(scope, "proj_a", "agent_y",
            id: "subs_1", name: "y-sub",
            filterType: "com.mohist.workflow.stage.*", priority: 1);

        var evt = BuildWorkflowEvent(
            type: "com.mohist.workflow.stage.approval-requested",
            workflowRunId: "wr_repeat");

        for (var i = 0; i < 3; i++)
        {
            recorder.Calls.Clear();
            await scope.Handler.HandleAsync(evt, CancellationToken.None);
            var launch = Assert.Single(recorder.Calls);
            Assert.Equal("y-sub", launch.Prompt);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task HandleAsync_ArchivedSubscription_DoesNotFire()
    {
        var (recorder, scope) = Build();
        await using var _ = scope;
        await SeedProjectAsync(scope, "proj_a");
        await SeedAgentAsync(scope, "proj_a", "agent_a", "agent-a");

        await SeedSubscriptionAsync(scope, "proj_a", "agent_a",
            id: "subs_active", name: "active",
            filterType: "com.mohist.workflow.stage.*", priority: 10);
        await SeedSubscriptionAsync(scope, "proj_a", "agent_a",
            id: "subs_archived", name: "archived",
            filterType: "com.mohist.workflow.stage.*", priority: 999); // higher, but archived

        await ArchiveSubscriptionAsync(scope, "proj_a", "subs_archived");

        var evt = BuildWorkflowEvent(
            type: "com.mohist.workflow.stage.approval-requested",
            workflowRunId: "wr_1");

        await scope.Handler.HandleAsync(evt, CancellationToken.None);

        var launch = Assert.Single(recorder.Calls);
        Assert.Equal("active", launch.Prompt);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task HandleAsync_ArchivedAgent_DoesNotFireItsSubscriptions()
    {
        var (recorder, scope) = Build();
        await using var _ = scope;
        await SeedProjectAsync(scope, "proj_a");
        await SeedAgentAsync(scope, "proj_a", "agent_active", "active-agent", status: AgentStatus.Active);
        await SeedAgentAsync(scope, "proj_a", "agent_archived", "archived-agent", status: AgentStatus.Archived);

        await SeedSubscriptionAsync(scope, "proj_a", "agent_active",
            name: "active-sub", filterType: "com.mohist.workflow.stage.*", priority: 5);
        await SeedSubscriptionAsync(scope, "proj_a", "agent_archived",
            name: "archived-sub", filterType: "com.mohist.workflow.stage.*", priority: 999);

        var evt = BuildWorkflowEvent(
            type: "com.mohist.workflow.stage.approval-requested",
            workflowRunId: "wr_1");

        await scope.Handler.HandleAsync(evt, CancellationToken.None);

        var launch = Assert.Single(recorder.Calls);
        Assert.Equal("active-sub", launch.Prompt);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task HandleAsync_IssueEventCarriesProjectIdExtension_DispatchesToIssueProjectSubscriptions()
    {
        var (recorder, scope) = Build();
        await using var _ = scope;
        await SeedProjectAsync(scope, "proj_a");
        await SeedAgentAsync(scope, "proj_a", "agent_a", "agent-a");

        await SeedSubscriptionAsync(scope, "proj_a", "agent_a",
            name: "issue-listener", filterType: "com.mohist.issue.*", priority: 5);

        // Issue events stamp projectid on extensions (IssueStore.SaveAsync).
        var evt = new CloudEvent(
            id: "evt_issue_1",
            source: new Uri("/mohist/issues/issue_x", UriKind.Relative),
            type: EventCatalog.ReverseDns.IssueWorkStarted,
            time: DateTimeOffset.UnixEpoch,
            data: null,
            extensions: new Dictionary<string, string>
            {
                ["projectid"] = "proj_a",
                ["issueid"] = "issue_x",
                ["issueno"] = "1",
            });

        await scope.Handler.HandleAsync(evt, CancellationToken.None);

        Assert.Equal("proj_a", Assert.Single(recorder.Calls).Context.ProjectId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task HandleAsync_WorkflowEventWithProductionEnvelope_Dispatches()
    {
        // WorkflowRunStore.ToCloudEvent now stamps projectid on the envelope
        // from the run's metadata annotations, so production workflow events
        // resolve and dispatch just like issue events.
        var (recorder, scope) = Build();
        await using var _ = scope;
        await SeedProjectAsync(scope, "proj_a");
        await SeedAgentAsync(scope, "proj_a", "agent_a", "agent-a");

        await SeedSubscriptionAsync(scope, "proj_a", "agent_a",
            name: "stage-watcher", filterType: "com.mohist.workflow.stage.*", priority: 5);

        var evt = BuildWorkflowEvent(
            type: EventCatalog.ReverseDns.StageApprovalRequested,
            workflowRunId: "wr_1",
            projectId: "proj_a");

        await scope.Handler.HandleAsync(evt, CancellationToken.None);

        var launch = Assert.Single(recorder.Calls);
        Assert.Equal("agent_a", launch.Agent.Id);
        Assert.Equal("proj_a", launch.Context.ProjectId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task HandleAsync_EventWithoutProjectIdExtension_DegradesToSkip()
    {
        // The envelope-only boundary: when an event (issue or workflow) does
        // not carry projectid, the handler skips rather than reverse-querying
        // any business domain.
        var (recorder, scope) = Build();
        await using var _ = scope;
        await SeedProjectAsync(scope, "proj_a");
        await SeedAgentAsync(scope, "proj_a", "agent_a", "agent-a");

        await SeedSubscriptionAsync(scope, "proj_a", "agent_a",
            name: "stage-watcher", filterType: "com.mohist.workflow.stage.*", priority: 5);

        var evt = BuildWorkflowEvent(
            type: EventCatalog.ReverseDns.StageApprovalRequested,
            workflowRunId: "wr_1",
            projectId: null);

        await scope.Handler.HandleAsync(evt, CancellationToken.None);

        Assert.Empty(recorder.Calls);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task HandleAsync_StampsTriggerLabelsOnLaunch()
    {
        var (recorder, scope) = Build();
        await using var _ = scope;
        await SeedProjectAsync(scope, "proj_a");
        await SeedAgentAsync(scope, "proj_a", "agent_a", "agent-a");

        await SeedSubscriptionAsync(scope, "proj_a", "agent_a",
            id: "subs_xyz", name: "watcher",
            filterType: "com.mohist.issue.*", priority: 5);

        var evt = new CloudEvent(
            id: "evt_trigger_42",
            source: new Uri("/mohist/issues/issue_x", UriKind.Relative),
            type: EventCatalog.ReverseDns.IssueWorkStarted,
            time: DateTimeOffset.UnixEpoch,
            data: null,
            extensions: new Dictionary<string, string> { ["projectid"] = "proj_a" });

        await scope.Handler.HandleAsync(evt, CancellationToken.None);

        var launch = Assert.Single(recorder.Calls);
        Assert.NotNull(launch.TriggerLabels);
        Assert.Equal("evt_trigger_42", launch.TriggerLabels![GenericAgentSessionMetadata.TriggerEventId]);
        Assert.Equal("subs_xyz", launch.TriggerLabels[GenericAgentSessionMetadata.TriggerSubscriptionId]);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task HandleAsync_LaunchFailure_PropagatesToDispatcher()
    {
        // issue-363 T-002: subscription launch failures now reach the
        // dispatcher's unified retry/DLQ path. The handler must NOT
        // absorb the exception via a local catch; instead it surfaces
        // the failure, the record of the launch attempt is preserved,
        // and no warning is logged at the handler boundary.
        var (recorder, scope) = Build();
        await using var _ = scope;
        await SeedProjectAsync(scope, "proj_a");
        await SeedAgentAsync(scope, "proj_a", "agent_a", "agent-a");
        await SeedSubscriptionAsync(scope, "proj_a", "agent_a",
            id: "subs_failure", name: "watcher",
            filterType: "com.mohist.issue.*", priority: 5);
        recorder.Failure = new InvalidOperationException("launch unavailable");

        var evt = new CloudEvent(
            id: "evt_failure",
            source: new Uri("/mohist/issues/issue_x", UriKind.Relative),
            type: EventCatalog.ReverseDns.IssueWorkStarted,
            time: DateTimeOffset.UnixEpoch,
            data: null,
            extensions: new Dictionary<string, string> { ["projectid"] = "proj_a" });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.Handler.HandleAsync(evt, CancellationToken.None));
        Assert.Equal("launch unavailable", ex.Message);

        Assert.Single(recorder.Calls);
        Assert.Empty(scope.Logger.Entries);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task HandleAsync_RenderedPromptReplacesVariablesBeforeLaunch()
    {
        var (recorder, scope) = Build();
        await using var _ = scope;
        await SeedProjectAsync(scope, "proj_a");
        await SeedAgentAsync(scope, "proj_a", "agent_a", "agent-a");

        await SeedSubscriptionAsync(scope, "proj_a", "agent_a",
            name: "templated",
            filterType: "com.mohist.workflow.stage.*",
            priority: 5,
            responsePrompt: "review run={{workflow_run_id}} stage={{stage}} event={{event_type}}");

        // Workflow event stamped with projectid so the handler will resolve it.
        var evt = BuildWorkflowEvent(
            type: EventCatalog.ReverseDns.StageApprovalRequested,
            workflowRunId: "wr_render",
            data: JsonSerializer.SerializeToElement(new { stage = "plan" }),
            extensions: new Dictionary<string, string> { ["projectid"] = "proj_a" });

        await scope.Handler.HandleAsync(evt, CancellationToken.None);

        var launch = Assert.Single(recorder.Calls);
        Assert.Equal(
            "review run=wr_render stage=plan event=com.mohist.workflow.stage.approval-requested",
            launch.Prompt);
    }

    private static async Task SeedSubscriptionAsync(
        TestScope scope,
        string projectId,
        string agentId,
        string? id = null,
        string? name = null,
        string filterType = "com.mohist.workflow.stage.*",
        int? priority = 0,
        string? source = null,
        string responsePrompt = "")
    {
        using var scope0 = NewWriteScope(scope);
        var store = scope0.ServiceProvider.GetRequiredService<AgentSubscriptionStore>();
        var actualId = id ?? $"subs_{Guid.NewGuid():N}";
        await store.CreateAsync(new AgentSubscription
        {
            Id = actualId,
            ProjectId = projectId,
            AgentId = agentId,
            Name = name ?? actualId,
            Filter = new SubscriptionFilter
            {
                Type = filterType,
                Source = source,
            },
            ResponsePrompt = string.IsNullOrEmpty(responsePrompt) ? (name ?? actualId) : responsePrompt,
            Priority = priority,
            Status = SubscriptionStatus.Active,
        });
    }

    private static async Task ArchiveSubscriptionAsync(TestScope scope, string projectId, string id)
    {
        using var scope0 = NewWriteScope(scope);
        var store = scope0.ServiceProvider.GetRequiredService<AgentSubscriptionStore>();
        await store.ArchiveAsync(id);
    }

    private static IServiceScope NewWriteScope(TestScope scope) =>
        BuildScope(scope);

    private static IServiceScope BuildScope(TestScope scope)
    {
        if (scope.Handler is null)
            throw new InvalidOperationException("handler missing");
        // Reflection-free access to the scope factory via test support.
        return ((IServiceScopeFactory)typeof(AgentSubscriptionDispatchHandler)
                .GetField("_scopeFactory", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(scope.Handler)!).CreateScope();
    }

    private static CloudEvent BuildWorkflowEvent(
        string type,
        string workflowRunId,
        JsonElement? data = null,
        IReadOnlyDictionary<string, string>? extensions = null,
        string? projectId = "proj_a") =>
        new(
            id: Guid.NewGuid().ToString(),
            source: new Uri($"/mohist/workflow-runs/{workflowRunId}", UriKind.Relative),
            type: type,
            time: DateTimeOffset.UnixEpoch,
            data: data,
            extensions: BuildExtensions(extensions, projectId));

    private static IReadOnlyDictionary<string, string>? BuildExtensions(
        IReadOnlyDictionary<string, string>? extra,
        string? projectId)
    {
        if (projectId is null && extra is null) return null;
        var dict = extra is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(extra, StringComparer.Ordinal);
        if (projectId is not null && !dict.ContainsKey("projectid"))
            dict["projectid"] = projectId;
        return dict.Count == 0 ? null : dict;
    }

    // The TestScope exposes Build via Build() in the support file; tests
    // call Build() directly via the static helper below.
    private static (RecordingAgentLauncher Launcher, TestScope Scope) Build() =>
        AgentSubscriptionDispatchTestSupport.Build();
}
