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
using Mohist.Server.Tests.Support;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.Tests.Specs.Notifications;

public sealed class HermesIssueNotificationSpecs
{
    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Foundation)]
    [Fact]
    public async Task ApprovalRequested_SendsPayloadWithStageBodyAndIssueScopedAction()
    {
        var harness = CreateHarness();

        await harness.Handler.HandleAsync(WorkflowEvent(
            EventCatalog.ReverseDns.StageApprovalRequested,
            "run_1",
            new StageApprovalRequested("plan")), CancellationToken.None);

        var payload = Assert.Single(harness.Client.Sent);
        Assert.Equal(NotificationKinds.ApprovalRequested, payload.NotificationType);
        Assert.Equal(42, payload.IssueNumber);
        Assert.Equal("Add Hermes outbound notifications", payload.IssueTitle);
        Assert.Equal("plan", payload.Stage);
        Assert.Equal("approve 42", payload.SuggestedAction);
        Assert.Contains("Issue #42 needs approval at stage plan", payload.Body, StringComparison.Ordinal);
        Assert.Contains("Next: approve 42", payload.Body, StringComparison.Ordinal);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Foundation)]
    [Fact]
    public async Task WorkflowFailed_SendsPayloadWithFailureReasonAndActionChoices()
    {
        var harness = CreateHarness();

        await harness.Handler.HandleAsync(WorkflowEvent(
            EventCatalog.ReverseDns.WorkflowRunFailed,
            "run_1",
            new WorkflowRunFailed("check task failed")), CancellationToken.None);

        var payload = Assert.Single(harness.Client.Sent);
        Assert.Equal(NotificationKinds.WorkflowFailed, payload.NotificationType);
        Assert.Equal("check task failed", payload.FailureReason);
        Assert.Equal("retry 42 or abandon 42", payload.SuggestedAction);
        Assert.Contains("Reason: check task failed", payload.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("stack", payload.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Foundation)]
    [Fact]
    public async Task IssueCompleted_SendsPayloadWithCompletionBody()
    {
        var harness = CreateHarness();

        await harness.Handler.HandleAsync(IssueEvent(
            EventCatalog.ReverseDns.IssueCompleted,
            new IssueCompleted("run_1")), CancellationToken.None);

        var payload = Assert.Single(harness.Client.Sent);
        Assert.Equal(NotificationKinds.IssueCompleted, payload.NotificationType);
        Assert.Equal("run_1", payload.WorkflowRunId);
        Assert.Equal("review issue 42", payload.SuggestedAction);
        Assert.Contains("Issue #42 completed", payload.Body, StringComparison.Ordinal);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Foundation)]
    [Fact]
    public async Task IssueStarted_IsDisabledByDefaultAndCanBeEnabled()
    {
        var defaultHarness = CreateHarness();
        await defaultHarness.Handler.HandleAsync(IssueEvent(
            EventCatalog.ReverseDns.IssueWorkStarted,
            new IssueWorkStarted("run_1")), CancellationToken.None);
        Assert.Empty(defaultHarness.Client.Sent);

        var enabledHarness = CreateHarness(new HermesNotificationOptions
        {
            WebhookUrl = "https://hermes.local/webhooks/mohist",
            EnabledTypes = [NotificationKinds.IssueStarted],
        });
        await enabledHarness.Handler.HandleAsync(IssueEvent(
            EventCatalog.ReverseDns.IssueWorkStarted,
            new IssueWorkStarted("run_1")), CancellationToken.None);

        var payload = Assert.Single(enabledHarness.Client.Sent);
        Assert.Equal(NotificationKinds.IssueStarted, payload.NotificationType);
        Assert.Contains("Issue #42 started", payload.Body, StringComparison.Ordinal);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Foundation)]
    [Fact]
    public async Task UnconfiguredWebhookUrl_DoesNotLoadStateOrSend()
    {
        var harness = CreateHarness(new HermesNotificationOptions { WebhookUrl = null });

        await harness.Handler.HandleAsync(WorkflowEvent(
            EventCatalog.ReverseDns.StageApprovalRequested,
            "run_1",
            new StageApprovalRequested("plan")), CancellationToken.None);

        Assert.Empty(harness.Client.Sent);
        Assert.Equal(0, harness.WorkflowRuns.LoadCount);
        Assert.Equal(0, harness.Issues.LoadCount);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Foundation)]
    [Fact]
    public async Task DisabledNotificationType_DoesNotLoadStateOrSend()
    {
        var harness = CreateHarness(new HermesNotificationOptions
        {
            WebhookUrl = "https://hermes.local/webhooks/mohist",
            EnabledTypes = [NotificationKinds.WorkflowFailed],
        });

        await harness.Handler.HandleAsync(WorkflowEvent(
            EventCatalog.ReverseDns.StageApprovalRequested,
            "run_1",
            new StageApprovalRequested("plan")), CancellationToken.None);

        Assert.Empty(harness.Client.Sent);
        Assert.Equal(0, harness.WorkflowRuns.LoadCount);
        Assert.Equal(0, harness.Issues.LoadCount);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Foundation)]
    [Fact]
    public async Task DeliveryFailure_IsSwallowed()
    {
        var harness = CreateHarness();
        harness.Client.ThrowOnSend = true;

        await harness.Handler.HandleAsync(WorkflowEvent(
            EventCatalog.ReverseDns.StageApprovalRequested,
            "run_1",
            new StageApprovalRequested("plan")), CancellationToken.None);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Foundation)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Foundation)]
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

    private static Harness CreateHarness(HermesNotificationOptions? options = null)
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
        var handler = new HermesIssueNotificationHandler(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new TestOptionsMonitor<HermesNotificationOptions>(options),
            new HermesIssueNotificationRenderer(),
            client,
            NullLogger<HermesIssueNotificationHandler>.Instance);

        return new Harness(handler, client, issues, workflowRuns, provider);
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

    private sealed record Harness(
        HermesIssueNotificationHandler Handler,
        RecordingHermesWebhookClient Client,
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

        public Task SendAsync(HermesIssueNotificationPayload payload, CancellationToken ct)
        {
            if (ThrowOnSend)
                throw new HttpRequestException("Hermes is unavailable");

            Sent.Add(payload);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeIssueStore : IStateStore<DomainIssue>
    {
        public Dictionary<string, DomainIssue> Items { get; } = new(StringComparer.Ordinal);
        public int LoadCount { get; private set; }

        public Task<DomainIssue?> LoadAsync(string key)
        {
            LoadCount++;
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
