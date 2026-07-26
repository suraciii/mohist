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
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Inbox;
using Mohist.Server.Issue.Domain.Events;
using Mohist.Server.Notifications;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;
namespace Mohist.Server.UnitTests.Notifications;

public abstract class HermesIssueNotificationTestSupport
{
    protected static NotificationFixture CreateFixture(HermesNotificationOptions? options = null)
    {
        options ??= new HermesNotificationOptions { WebhookUrl = "https://hermes.local/webhooks/mohist" };
        var issues = new FakeIssueStore();
        var workflowRuns = new FakeWorkflowRunStore();
        issues.Items[GrainKey.Issue(new IssueKey("proj_1", 42))] = new DomainIssue
        {
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

    protected static CloudEvent WorkflowEvent<T>(
        string type,
        string workflowRunId,
        T data,
        string? envelopeStage = null) where T : class
    {
        var extensions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [EventCatalog.Lineage.ProjectId] = "proj_1",
            [EventCatalog.Lineage.Issue] = "42",
            [EventCatalog.Lineage.WorkflowRunId] = workflowRunId,
        };
        if (envelopeStage is not null)
            extensions[EventCatalog.Lineage.Stage] = envelopeStage;
        else if (data is StageApprovalRequested approval)
            extensions[EventCatalog.Lineage.Stage] = approval.Stage;

        return new(
            id: "evt_" + type.Replace(".", "_", StringComparison.Ordinal),
            source: new Uri("/mohist/workflow-runs/" + workflowRunId, UriKind.Relative),
            type: type,
            time: new DateTimeOffset(2026, 7, 3, 12, 1, 0, TimeSpan.Zero),
            data: JsonSerializer.SerializeToElement(data, CloudEvent.JsonOptions),
            extensions: extensions);
    }

    protected static CloudEvent IssueEvent<T>(string type, T data) where T : class =>
        new(
            id: "evt_" + type.Replace(".", "_", StringComparison.Ordinal),
            source: new Uri("/mohist/projects/proj_1/issues/42", UriKind.Relative),
            type: type,
            time: new DateTimeOffset(2026, 7, 3, 12, 1, 0, TimeSpan.Zero),
            data: JsonSerializer.SerializeToElement(data, CloudEvent.JsonOptions),
            extensions: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["projectid"] = "proj_1",
                [EventCatalog.Lineage.Issue] = "42",
            });

    protected static CloudEvent AgentJobFailedEvent(string failureReason) =>
        new(
            id: "evt_agent_job_failed",
            source: new Uri("/mohist/agent-job/job_1", UriKind.Relative),
            type: EventCatalog.ReverseDns.AgentJobFailed,
            time: new DateTimeOffset(2026, 7, 3, 12, 1, 0, TimeSpan.Zero),
            data: JsonSerializer.SerializeToElement(new { failureReason }, CloudEvent.JsonOptions),
            extensions: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [EventCatalog.Lineage.AgentId] = "agent_1",
                [EventCatalog.Lineage.ProjectId] = "proj_1",
                [EventCatalog.Lineage.Issue] = "42",
            });

    protected static CloudEvent IssueEventUnified<T>(string type, T data) where T : class =>
        new(
            id: "evt_unified_" + type.Replace(".", "_", StringComparison.Ordinal),
            source: new Uri("/mohist/projects/proj_1/issues/42", UriKind.Relative),
            type: type,
            time: new DateTimeOffset(2026, 7, 3, 12, 1, 0, TimeSpan.Zero),
            data: JsonSerializer.SerializeToElement(data, CloudEvent.JsonOptions),
            extensions: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [EventCatalog.Lineage.ProjectId] = "proj_1",
                [EventCatalog.Lineage.Issue] = "42",
            });

    protected static CloudEvent IssueEventNoIssueNumber<T>(string type, T data) where T : class =>
        new(
            id: "evt_nonum_" + type.Replace(".", "_", StringComparison.Ordinal),
            source: new Uri("/mohist/projects/proj_1/issues/42", UriKind.Relative),
            type: type,
            time: new DateTimeOffset(2026, 7, 3, 12, 1, 0, TimeSpan.Zero),
            data: JsonSerializer.SerializeToElement(data, CloudEvent.JsonOptions),
            extensions: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [EventCatalog.Lineage.ProjectId] = "proj_1",
            });

    protected static HermesIssueNotificationPayload SamplePayload() =>
        new(
            NotificationKinds.IssueCompleted,
            EventCatalog.ReverseDns.IssueCompleted,
            "evt_1",
            new DateTimeOffset(2026, 7, 3, 12, 0, 0, TimeSpan.Zero),
             "proj_1",
             42,
             null,
             "Title",
            "run_1",
            null,
            null,
            "review issue 42",
            "Issue #42 completed");

    protected static string Sign(string body, string secret)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body));
        return "sha256=" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    protected sealed record NotificationFixture(
        HermesIssueNotificationHandler Handler,
        RecordingHermesWebhookClient Client,
        RecordingHermesIssueNotificationDispatcher Dispatcher,
        FakeIssueStore Issues,
        FakeWorkflowRunStore WorkflowRuns,
        ServiceProvider Provider) : IDisposable
    {
        public void Dispose() => Provider.Dispose();
    }

    protected sealed class RecordingHermesWebhookClient : IHermesWebhookClient
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

    protected sealed class RecordingHermesIssueNotificationDispatcher : IHermesIssueNotificationDispatcher
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

    protected sealed class FakeIssueStore : IStateStore<DomainIssue>
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

    protected sealed class FakeWorkflowRunStore : IWorkflowRunStore
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

        public Task DeleteAsync(string workflowRunId, CancellationToken ct = default)
        {
            Items.Remove(workflowRunId);
            return Task.CompletedTask;
        }

    }

    protected sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public TestOptionsMonitor(T value) => CurrentValue = value;
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    protected sealed class RecordingHttpHandler : HttpMessageHandler
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

    protected sealed record RecordedRequest(
        HttpMethod Method,
        string Uri,
        string Body,
        string? SignatureHeader,
        string? EventHeader);
}
