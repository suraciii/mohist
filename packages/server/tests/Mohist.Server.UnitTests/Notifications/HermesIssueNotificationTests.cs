using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mohist.Server.Infrastructure.Data;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Inbox;
using Mohist.Server.Issue.Domain.Events;
using Mohist.Server.Notifications;
using Mohist.Server.Notifications.Subscriptions;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.UnitTests.Notifications;

public sealed class HermesIssueNotificationTests : HermesIssueNotificationTestSupport
{
    [Fact]
    public async Task ApprovalRequested_SendsPayloadWithStageBodyAndIssueScopedAction()
    {
        var fixture = CreateFixture();

        await fixture.Handler.HandleAsync(WorkflowEvent(
            EventCatalog.ReverseDns.StageApprovalRequested,
            "run_1",
            new StageApprovalRequested("plan")), CancellationToken.None);
        await fixture.Dispatcher.RunAllAsync();

        var payload = Assert.Single(fixture.Client.Sent);
        Assert.Equal(NotificationKinds.ApprovalRequested, payload.NotificationType);
        Assert.Equal(42, payload.IssueNumber);
        Assert.Equal("Add Hermes outbound notifications", payload.IssueTitle);
        Assert.Equal("plan", payload.Stage);
        Assert.Equal("approve 42", payload.SuggestedAction);
        Assert.Contains("Issue #42 needs approval at stage plan", payload.Body, StringComparison.Ordinal);
        Assert.Contains("Next: approve 42", payload.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkflowFailed_SendsPayloadWithFailureReasonAndActionChoices()
    {
        var fixture = CreateFixture();

        await fixture.Handler.HandleAsync(WorkflowEvent(
            EventCatalog.ReverseDns.WorkflowRunFailed,
            "run_1",
            new WorkflowRunFailed("check task failed")), CancellationToken.None);
        await fixture.Dispatcher.RunAllAsync();

        var payload = Assert.Single(fixture.Client.Sent);
        Assert.Equal(NotificationKinds.WorkflowFailed, payload.NotificationType);
        Assert.Equal("check task failed", payload.FailureReason);
        Assert.Equal("retry 42 or abandon 42", payload.SuggestedAction);
        Assert.Contains("Reason: check task failed", payload.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("stack", payload.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BlockedAgentResult_IsNotAHermesFailureNotification()
    {
        var fixture = CreateFixture();
        var evt = WorkflowEvent(
            EventCatalog.ReverseDns.WorkflowRunBlocked,
            "run_1",
            new WorkflowRunBlocked("build", "build.1", "agent-result-unconfirmed", TestTime.UtcNow));

        Assert.False(fixture.Handler.Filter(evt));
        await fixture.Handler.HandleAsync(evt, CancellationToken.None);
        await fixture.Dispatcher.RunAllAsync();

        Assert.Empty(fixture.Client.Sent);
    }

    [Fact]
    public async Task AgentJobFailed_IsEnabledByDefaultAndSendsFailurePayload()
    {
        var fixture = CreateFixture();

        await fixture.Handler.HandleAsync(AgentJobFailedEvent("runner unavailable"), CancellationToken.None);
        await fixture.Dispatcher.RunAllAsync();

        var payload = Assert.Single(fixture.Client.Sent);
        Assert.Equal(NotificationKinds.AgentResponseFailed, payload.NotificationType);
        Assert.Equal("runner unavailable", payload.FailureReason);
        Assert.Contains("Agent response failed for issue #42", payload.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkflowFailed_OmitsStackTraceLinesFromFailurePayloadAndBody()
    {
        var fixture = CreateFixture();
        var failure = "System.InvalidOperationException: check task failed\n"
            + "   at Mohist.Server.Workflow.Domain.Run.WorkflowRun.Fail() in WorkflowRun.cs:line 55\n"
            + "   at Mohist.Server.Workflow.Domain.Run.WorkflowRun.Check() in WorkflowRun.Check.cs:line 75";

        await fixture.Handler.HandleAsync(WorkflowEvent(
            EventCatalog.ReverseDns.WorkflowRunFailed,
            "run_1",
            new WorkflowRunFailed(failure)), CancellationToken.None);
        await fixture.Dispatcher.RunAllAsync();

        var payload = Assert.Single(fixture.Client.Sent);
        Assert.Equal("System.InvalidOperationException: check task failed", payload.FailureReason);
        Assert.Contains("Reason: System.InvalidOperationException: check task failed", payload.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("WorkflowRun.Fail", payload.FailureReason, StringComparison.Ordinal);
        Assert.DoesNotContain("WorkflowRun.Fail", payload.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(" at Mohist.Server", payload.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IssueCompleted_SendsPayloadWithCompletionBody()
    {
        var fixture = CreateFixture();

        await fixture.Handler.HandleAsync(IssueEvent(
            EventCatalog.ReverseDns.IssueCompleted,
            new IssueCompleted("run_1")), CancellationToken.None);
        await fixture.Dispatcher.RunAllAsync();

        var payload = Assert.Single(fixture.Client.Sent);
        Assert.Equal(NotificationKinds.IssueCompleted, payload.NotificationType);
        Assert.Equal("run_1", payload.WorkflowRunId);
        Assert.Equal("review issue 42", payload.SuggestedAction);
        Assert.Contains("Issue #42 completed", payload.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IssueEvent_RoutesByEnvelopeAndPreservesPayloadWorkflowRunId()
    {
        var fixture = CreateFixture();
        var payload = new IssueCompleted("payload-run");
        var evt = new CloudEvent(
            id: "evt-envelope-context",
            source: new Uri("/mohist/projects/proj_1/issues/99", UriKind.Relative),
            type: EventCatalog.ReverseDns.IssueCompleted,
            time: new DateTimeOffset(2026, 7, 3, 12, 1, 0, TimeSpan.Zero),
            data: JsonSerializer.SerializeToElement(payload, CloudEvent.JsonOptions),
            extensions: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [EventCatalog.Lineage.ProjectId] = "proj_1",
                [EventCatalog.Lineage.Issue] = "42",
                [EventCatalog.Lineage.Epic] = "7",
            });

        await fixture.Handler.HandleAsync(evt, CancellationToken.None);
        await fixture.Dispatcher.RunAllAsync();

        var sent = Assert.Single(fixture.Client.Sent);
        Assert.Equal(42, sent.IssueNumber);
        Assert.Equal(7, sent.EpicNumber);
        Assert.Equal("payload-run", sent.WorkflowRunId);
        Assert.Equal("payload-run", evt.Data!.Value.GetProperty("workflowRunId").GetString());
    }

    [Fact]
    public async Task ApprovalRequested_UsesEnvelopeStageWithoutMutatingPayload()
    {
        var fixture = CreateFixture();
        var payload = new StageApprovalRequested("payload-stage");
        var evt = WorkflowEvent(
            EventCatalog.ReverseDns.StageApprovalRequested,
            "run_1",
            payload,
            envelopeStage: "envelope-stage");

        await fixture.Handler.HandleAsync(evt, CancellationToken.None);
        await fixture.Dispatcher.RunAllAsync();

        var sent = Assert.Single(fixture.Client.Sent);
        Assert.Equal("envelope-stage", sent.Stage);
        Assert.Equal("payload-stage", evt.Data!.Value.GetProperty("stage").GetString());
    }

    [Fact]
    public async Task IssueStarted_IsDisabledByDefaultAndCanBeEnabled()
    {
        var defaultFixture = CreateFixture();
        await defaultFixture.Handler.HandleAsync(IssueEvent(
            EventCatalog.ReverseDns.IssueWorkStarted,
            new IssueWorkStarted("run_1")), CancellationToken.None);
        await defaultFixture.Dispatcher.RunAllAsync();
        Assert.Empty(defaultFixture.Client.Sent);

        var enabledFixture = CreateFixture(new HermesNotificationOptions
        {
            WebhookUrl = "https://hermes.local/webhooks/mohist",
            EnabledTypes = [NotificationKinds.IssueStarted],
        });
        await enabledFixture.Handler.HandleAsync(IssueEvent(
            EventCatalog.ReverseDns.IssueWorkStarted,
            new IssueWorkStarted("run_1")), CancellationToken.None);
        await enabledFixture.Dispatcher.RunAllAsync();

        var payload = Assert.Single(enabledFixture.Client.Sent);
        Assert.Equal(NotificationKinds.IssueStarted, payload.NotificationType);
        Assert.Contains("Issue #42 started", payload.Body, StringComparison.Ordinal);
    }

    // --- T-007: issueno -> issue rename; dual-key read for historical rows ---

    [Fact]
    public async Task IssueEvent_UnifiedIssueKey_ResolvesIdentity()
    {
        var fixture = CreateFixture();

        await fixture.Handler.HandleAsync(IssueEventUnified(
            EventCatalog.ReverseDns.IssueCompleted,
            new IssueCompleted("run_1")), CancellationToken.None);
        await fixture.Dispatcher.RunAllAsync();

        // Post-change row stamped with the unified `issue` key. The
        // handler must resolve identity from `issue` directly.
        var payload = Assert.Single(fixture.Client.Sent);
        Assert.Equal(NotificationKinds.IssueCompleted, payload.NotificationType);
        Assert.Equal(42, payload.IssueNumber);
        Assert.Equal("Add Hermes outbound notifications", payload.IssueTitle);
    }

    [Fact]
    public async Task IssueEvent_NoIssueNumberKey_SkipsWithoutSending()
    {
        var fixture = CreateFixture();

        await fixture.Handler.HandleAsync(IssueEventNoIssueNumber(
            EventCatalog.ReverseDns.IssueCompleted,
            new IssueCompleted("run_1")), CancellationToken.None);
        await fixture.Dispatcher.RunAllAsync();

        // Neither `issue` nor `issueno` is present: identity cannot be
        // resolved, the handler skips silently.
        Assert.Empty(fixture.Client.Sent);
    }

    [Fact]
    public async Task UnconfiguredWebhookUrl_DoesNotLoadStateOrSend()
    {
        var fixture = CreateFixture(new HermesNotificationOptions { WebhookUrl = null });

        await fixture.Handler.HandleAsync(AgentJobFailedEvent("runner unavailable"), CancellationToken.None);

        Assert.Empty(fixture.Client.Sent);
        Assert.Equal(0, fixture.Dispatcher.QueuedCount);
        Assert.Equal(0, fixture.WorkflowRuns.LoadCount);
        Assert.Equal(0, fixture.Issues.LoadCount);
    }

    [Fact]
    public async Task DisabledNotificationType_DoesNotLoadStateOrSend()
    {
        var fixture = CreateFixture(new HermesNotificationOptions
        {
            WebhookUrl = "https://hermes.local/webhooks/mohist",
            EnabledTypes = [NotificationKinds.WorkflowFailed],
        });

        await fixture.Handler.HandleAsync(WorkflowEvent(
            EventCatalog.ReverseDns.StageApprovalRequested,
            "run_1",
            new StageApprovalRequested("plan")), CancellationToken.None);

        Assert.Empty(fixture.Client.Sent);
        Assert.Equal(0, fixture.Dispatcher.QueuedCount);
        Assert.Equal(0, fixture.WorkflowRuns.LoadCount);
        Assert.Equal(0, fixture.Issues.LoadCount);
    }

    [Fact]
    public async Task DeliveryFailure_IsSwallowed()
    {
        var fixture = CreateFixture();
        fixture.Client.ThrowOnSend = true;

        await fixture.Handler.HandleAsync(WorkflowEvent(
            EventCatalog.ReverseDns.StageApprovalRequested,
            "run_1",
            new StageApprovalRequested("plan")), CancellationToken.None);
        await fixture.Dispatcher.RunAllAsync();
        Assert.Empty(fixture.Client.Sent);
    }

    [Fact]
    public async Task SetupFailure_PropagatesToDispatcher()
    {
        // issue-363 T-002: setup/enqueue failures no longer get caught at
        // the handler boundary. When the background dispatcher itself
        // fails to enqueue, the exception reaches the durable dispatcher
        // for retry/dead-lettering. The handler is synchronous here, so
        // we directly use a dispatcher whose Dispatch call throws —
        // matching a real failure mode (channel full / broker down).
        var fixture = CreateFixture();
        fixture.Dispatcher.ThrowOnDispatch = new InvalidOperationException("dispatcher unavailable");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Handler.HandleAsync(WorkflowEvent(
                EventCatalog.ReverseDns.StageApprovalRequested,
                "run_1",
                new StageApprovalRequested("plan")), CancellationToken.None));
        Assert.Equal("dispatcher unavailable", ex.Message);
        Assert.Equal(0, fixture.Dispatcher.QueuedCount);
        Assert.Empty(fixture.Client.Sent);
    }

    [Fact]
    public async Task DeliveryWork_IsQueuedWithoutAwaitingSlowWebhookSend()
    {
        var fixture = CreateFixture();
        fixture.Client.BlockSend = true;

        await fixture.Handler.HandleAsync(WorkflowEvent(
            EventCatalog.ReverseDns.StageApprovalRequested,
            "run_1",
            new StageApprovalRequested("plan")), CancellationToken.None);

        Assert.Equal(1, fixture.Dispatcher.QueuedCount);
        Assert.Empty(fixture.Client.Sent);

        var delivery = fixture.Dispatcher.RunNextAsync();
        await fixture.Client.SendStarted.Task;

        Assert.False(delivery.IsCompleted);
        Assert.Empty(fixture.Client.Sent);
        fixture.Client.ReleaseSend();
        await delivery;
        Assert.Single(fixture.Client.Sent);
    }

    [Fact]
    public async Task IssueLoadFailure_IsSwallowed()
    {
        var fixture = CreateFixture();
        fixture.Issues.LoadFailure = new InvalidOperationException("issue store unavailable");

        await fixture.Handler.HandleAsync(IssueEvent(
            EventCatalog.ReverseDns.IssueCompleted,
            new IssueCompleted("run_1")), CancellationToken.None);
        await fixture.Dispatcher.RunAllAsync();
        Assert.Empty(fixture.Client.Sent);
    }

    [Fact]
    public async Task WebhookClient_SignsJsonPayloadWhenSecretIsConfigured()
    {
        var handler = new RecordingHttpHandler();
        var options = new TestOptionsMonitor<HermesNotificationOptions>(new HermesNotificationOptions
        {
            WebhookUrl = "https://hermes.local/webhooks/mohist",
            Secret = "shared-secret",
        });
        var client = new HermesWebhookClient(new HttpClient(handler), options);
        var payload = new HermesIssueNotificationPayload(
            NotificationKinds.ApprovalRequested,
            EventCatalog.ReverseDns.StageApprovalRequested,
            "evt_1",
            new DateTimeOffset(2026, 7, 3, 12, 0, 0, TimeSpan.Zero),
             "proj_1",
             42,
             null,
             "Title",
            "run_1",
            "plan",
            null,
            "approve 42",
            "Issue #42 needs approval");

        await client.SendAsync(payload, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://hermes.local/webhooks/mohist", request.Uri);
        Assert.Equal(NotificationKinds.ApprovalRequested, request.EventHeader);
        Assert.Equal(Sign(request.Body, "shared-secret"), request.SignatureHeader);

        using var doc = JsonDocument.Parse(request.Body);
        Assert.Equal("approval_requested", doc.RootElement.GetProperty("notificationType").GetString());
        Assert.Equal(42, doc.RootElement.GetProperty("issueNumber").GetInt32());
        Assert.Equal("Issue #42 needs approval", doc.RootElement.GetProperty("body").GetString());
    }

    [Fact]
    public async Task WebhookClient_UnconfiguredUrlDoesNotSendNetworkRequest()
    {
        var handler = new RecordingHttpHandler();
        var client = new HermesWebhookClient(
            new HttpClient(handler),
            new TestOptionsMonitor<HermesNotificationOptions>(new HermesNotificationOptions()));

        await client.SendAsync(SamplePayload(), CancellationToken.None);

        Assert.Empty(handler.Requests);
    }

}
