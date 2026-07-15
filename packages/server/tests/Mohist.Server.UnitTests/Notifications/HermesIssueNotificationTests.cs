using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mohist.Server.Events.Subscriptions;
using Mohist.Server.Infrastructure.Data;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Inbox;
using Mohist.Server.Issue.Domain.Events;
using Mohist.Server.Notifications;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.UnitTests.Notifications;

public sealed class HermesIssueNotificationTests
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
    public async Task IssueEvent_LegacyIssuenoFallback_ResolvesIdentity()
    {
        var fixture = CreateFixture();

        await fixture.Handler.HandleAsync(IssueEventLegacy(
            EventCatalog.ReverseDns.IssueCompleted,
            new IssueCompleted("run_1")), CancellationToken.None);
        await fixture.Dispatcher.RunAllAsync();

        // Pre-change historical row stamped with the legacy `issueno`
        // key. The dual-key read must still resolve identity — the
        // Non-Goal forbids backfill.
        var payload = Assert.Single(fixture.Client.Sent);
        Assert.Equal(NotificationKinds.IssueCompleted, payload.NotificationType);
        Assert.Equal(42, payload.IssueNumber);
        Assert.Equal("Add Hermes outbound notifications", payload.IssueTitle);
    }

    [Fact]
    public async Task IssueEvent_BothKeysPresent_PrefersUnifiedIssueKey()
    {
        var fixture = CreateFixture();

        await fixture.Handler.HandleAsync(IssueEventBothKeys(
            EventCatalog.ReverseDns.IssueCompleted,
            new IssueCompleted("run_1")), CancellationToken.None);
        await fixture.Dispatcher.RunAllAsync();

        // Both keys stamped, but `issue` carries the right number and
        // `issueno` disagrees. The unified key wins.
        var payload = Assert.Single(fixture.Client.Sent);
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
            "issue_1",
            42,
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

    private static NotificationFixture CreateFixture(HermesNotificationOptions? options = null)
    {
        options ??= new HermesNotificationOptions { WebhookUrl = "https://hermes.local/webhooks/mohist" };
        var issues = new FakeIssueStore();
        var workflowRuns = new FakeWorkflowRunStore();
        issues.Items["issue_1"] = new DomainIssue
        {
            Id = "issue_1",
            ProjectId = "proj_1",
            Number = 42,
            Title = "Add Hermes outbound notifications",
        };
        workflowRuns.Items["run_1"] = new WorkflowRun
        {
            Id = "run_1",
            Metadata = new WorkflowRunMetadata(
                Name: null,
                CreatedAt: new DateTimeOffset(2026, 7, 3, 12, 0, 0, TimeSpan.Zero),
                Annotations: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["projectId"] = "proj_1",
                    ["issueId"] = "issue_1",
                    ["issueNumber"] = "42",
                }),
            Status = WorkflowRunStatus.Running,
            Stages = [],
        };

        var services = new ServiceCollection();
        services.AddSingleton<IStateStore<DomainIssue>>(issues);
        services.AddSingleton<IWorkflowRunStore>(workflowRuns);
        var provider = services.BuildServiceProvider();
        var client = new RecordingHermesWebhookClient();
        var dispatcher = new RecordingHermesIssueNotificationDispatcher();
        var handler = new HermesIssueNotificationHandler(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new TestOptionsMonitor<HermesNotificationOptions>(options),
            new HermesIssueNotificationRenderer(),
            client,
            dispatcher,
            NullLogger<HermesIssueNotificationHandler>.Instance);

        return new NotificationFixture(handler, client, dispatcher, issues, workflowRuns, provider);
    }

    private static CloudEvent WorkflowEvent<T>(string type, string workflowRunId, T data) where T : class =>
        new(
            id: "evt_" + type.Replace(".", "_", StringComparison.Ordinal),
            source: new Uri("/mohist/workflow-runs/" + workflowRunId, UriKind.Relative),
            type: type,
            time: new DateTimeOffset(2026, 7, 3, 12, 1, 0, TimeSpan.Zero),
            data: JsonSerializer.SerializeToElement(data, CloudEvent.JsonOptions));

    private static CloudEvent IssueEvent<T>(string type, T data) where T : class =>
        new(
            id: "evt_" + type.Replace(".", "_", StringComparison.Ordinal),
            source: new Uri("/mohist/issues/issue_1", UriKind.Relative),
            type: type,
            time: new DateTimeOffset(2026, 7, 3, 12, 1, 0, TimeSpan.Zero),
            data: JsonSerializer.SerializeToElement(data, CloudEvent.JsonOptions),
            extensions: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["projectid"] = "proj_1",
                ["issueid"] = "issue_1",
                ["issueno"] = "42",
            });

    private static CloudEvent IssueEventUnified<T>(string type, T data) where T : class =>
        new(
            id: "evt_unified_" + type.Replace(".", "_", StringComparison.Ordinal),
            source: new Uri("/mohist/issues/issue_1", UriKind.Relative),
            type: type,
            time: new DateTimeOffset(2026, 7, 3, 12, 1, 0, TimeSpan.Zero),
            data: JsonSerializer.SerializeToElement(data, CloudEvent.JsonOptions),
            extensions: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [EventCatalog.Lineage.ProjectId] = "proj_1",
                [EventCatalog.Lineage.IssueId] = "issue_1",
                [EventCatalog.Lineage.Issue] = "42",
            });

    private static CloudEvent IssueEventLegacy<T>(string type, T data) where T : class =>
        new(
            id: "evt_legacy_" + type.Replace(".", "_", StringComparison.Ordinal),
            source: new Uri("/mohist/issues/issue_1", UriKind.Relative),
            type: type,
            time: new DateTimeOffset(2026, 7, 3, 12, 1, 0, TimeSpan.Zero),
            data: JsonSerializer.SerializeToElement(data, CloudEvent.JsonOptions),
            extensions: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [EventCatalog.Lineage.ProjectId] = "proj_1",
                [EventCatalog.Lineage.IssueId] = "issue_1",
                ["issueno"] = "42",
            });

    private static CloudEvent IssueEventBothKeys<T>(string type, T data) where T : class =>
        new(
            id: "evt_both_" + type.Replace(".", "_", StringComparison.Ordinal),
            source: new Uri("/mohist/issues/issue_1", UriKind.Relative),
            type: type,
            time: new DateTimeOffset(2026, 7, 3, 12, 1, 0, TimeSpan.Zero),
            data: JsonSerializer.SerializeToElement(data, CloudEvent.JsonOptions),
            extensions: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [EventCatalog.Lineage.ProjectId] = "proj_1",
                [EventCatalog.Lineage.IssueId] = "issue_1",
                [EventCatalog.Lineage.Issue] = "42",
                ["issueno"] = "999",
            });

    private static CloudEvent IssueEventNoIssueNumber<T>(string type, T data) where T : class =>
        new(
            id: "evt_nonum_" + type.Replace(".", "_", StringComparison.Ordinal),
            source: new Uri("/mohist/issues/issue_1", UriKind.Relative),
            type: type,
            time: new DateTimeOffset(2026, 7, 3, 12, 1, 0, TimeSpan.Zero),
            data: JsonSerializer.SerializeToElement(data, CloudEvent.JsonOptions),
            extensions: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [EventCatalog.Lineage.ProjectId] = "proj_1",
                [EventCatalog.Lineage.IssueId] = "issue_1",
            });

    private static HermesIssueNotificationPayload SamplePayload() =>
        new(
            NotificationKinds.IssueCompleted,
            EventCatalog.ReverseDns.IssueCompleted,
            "evt_1",
            new DateTimeOffset(2026, 7, 3, 12, 0, 0, TimeSpan.Zero),
            "proj_1",
            "issue_1",
            42,
            "Title",
            "run_1",
            null,
            null,
            "review issue 42",
            "Issue #42 completed");

    private static string Sign(string body, string secret)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body));
        return "sha256=" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed record NotificationFixture(
        HermesIssueNotificationHandler Handler,
        RecordingHermesWebhookClient Client,
        RecordingHermesIssueNotificationDispatcher Dispatcher,
        FakeIssueStore Issues,
        FakeWorkflowRunStore WorkflowRuns,
        ServiceProvider Provider) : IDisposable
    {
        public void Dispose() => Provider.Dispose();
    }

    private sealed class RecordingHermesWebhookClient : IHermesWebhookClient
    {
        public List<HermesIssueNotificationPayload> Sent { get; } = [];
        public bool ThrowOnSend { get; set; }
        public bool BlockSend { get; set; }
        public TaskCompletionSource SendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseSend = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task SendAsync(HermesIssueNotificationPayload payload, CancellationToken ct)
        {
            SendStarted.TrySetResult();

            if (BlockSend)
                await _releaseSend.Task.WaitAsync(ct).ConfigureAwait(false);

            if (ThrowOnSend)
                throw new HttpRequestException("Hermes is unavailable");

            Sent.Add(payload);
        }

        public void ReleaseSend() => _releaseSend.TrySetResult();
    }

    private sealed class RecordingHermesIssueNotificationDispatcher : IHermesIssueNotificationDispatcher
    {
        private readonly Queue<Func<CancellationToken, Task>> _works = new();

        public int QueuedCount => _works.Count;
        public Exception? ThrowOnDispatch { get; set; }

        public void Dispatch(Func<CancellationToken, Task> work)
        {
            if (ThrowOnDispatch is not null)
                throw ThrowOnDispatch;
            _works.Enqueue(work);
        }

        public async Task RunAllAsync(CancellationToken ct = default)
        {
            while (_works.Count > 0)
                await RunNextAsync(ct).ConfigureAwait(false);
        }

        public Task RunNextAsync(CancellationToken ct = default) => _works.Dequeue()(ct);
    }

    private sealed class FakeIssueStore : IStateStore<DomainIssue>
    {
        public Dictionary<string, DomainIssue> Items { get; } = new(StringComparer.Ordinal);
        public int LoadCount { get; private set; }
        public Exception? LoadFailure { get; set; }

        public Task<DomainIssue?> LoadAsync(string key)
        {
            LoadCount++;
            if (LoadFailure is not null)
                return Task.FromException<DomainIssue?>(LoadFailure);
            Items.TryGetValue(key, out var issue);
            return Task.FromResult(issue);
        }

        public Task<IReadOnlyList<DomainIssue>> ListAsync() => Task.FromResult<IReadOnlyList<DomainIssue>>(Items.Values.ToList());
        public Task SaveAsync(string key, DomainIssue state) { Items[key] = state; return Task.CompletedTask; }
        public Task DeleteAsync(string key) { Items.Remove(key); return Task.CompletedTask; }
    }

    private sealed class FakeWorkflowRunStore : IWorkflowRunStore
    {
        public Dictionary<string, WorkflowRun> Items { get; } = new(StringComparer.Ordinal);
        public int LoadCount { get; private set; }

        public Task SaveAsync(WorkflowRun run, CancellationToken ct = default)
        {
            Items[run.Id] = run;
            return Task.CompletedTask;
        }

        public Task SaveAsync(WorkflowRun run, IReadOnlyList<WorkflowEvent> events, CancellationToken ct = default) => SaveAsync(run, ct);

        public Task<WorkflowRun?> LoadAsync(string workflowRunId, CancellationToken ct = default)
        {
            LoadCount++;
            Items.TryGetValue(workflowRunId, out var run);
            return Task.FromResult(run);
        }
    }

    private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public TestOptionsMonitor(T value) => CurrentValue = value;
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class RecordingHttpHandler : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri?.ToString() ?? string.Empty,
                body,
                request.Headers.TryGetValues(HermesWebhookClient.SignatureHeader, out var signatures) ? signatures.SingleOrDefault() : null,
                request.Headers.TryGetValues(HermesWebhookClient.EventHeader, out var events) ? events.SingleOrDefault() : null));
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        string Uri,
        string Body,
        string? SignatureHeader,
        string? EventHeader);
}
