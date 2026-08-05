using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Agent.Subscriptions;
using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Runner.Services;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
using Orleans;
using AgentDomain = Mohist.Server.Agent.Domain.Agent;
using AgentRow = Mohist.Server.Infrastructure.Data.Agent.AgentRow;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;
using IssueStore = Mohist.Server.Infrastructure.Data.Issue.IssueStore;

namespace Mohist.Server.SpecTests.Support;

/// <summary>
/// Shared support for the issue-watch-dispatch spec suite. Each spec
/// stands up a fresh migrated SQLite, seeds the Agent / project /
/// routing-rule / workflow-run / watch-entry rows it needs, drives the
/// production <see cref="RoutingDispatchHandler"/> with a single
/// CloudEvent envelope, and inspects the captured
/// <see cref="RecordingAgentLauncher"/> log to assert that the right
/// launches fired (with the right prompt, the right TriggerRuleId, and
/// the right preflight status).
///
/// <para>
/// The suite replaces the real <see cref="IAgentLauncher"/> with
/// <see cref="RecordingAgentLauncher"/> so the handler is exercised
/// end-to-end without an Orleans silo — the launcher's job is to invoke
/// the AgentJobGrain's <c>EnsurePreparedAsync</c>, which is faked via
/// the captured launch plans.
/// </para>
/// </summary>
internal static class RoutingDispatchTestSupport
{
    public static readonly DateTimeOffset FixedEventTime = new(2026, 7, 15, 0, 0, 0, TimeSpan.Zero);

    public static TestSqliteDatabase CreateDatabase() => TestSqliteDatabase.CreateMigrated();

    public static IServiceScopeFactory CreateScopeFactory(
        TestSqliteDatabase database,
        RecordingAgentLauncher launcher,
        Action<IServiceCollection>? configure = null) =>
        new DispatchScopeFactory(database, launcher, configure);

    public static RoutingDispatchHandler CreateHandler(IServiceScopeFactory scopeFactory) =>
        new(scopeFactory, NullLogger<RoutingDispatchHandler>.Instance);

    public static MentionDispatchHandler CreateMentionHandler(IServiceScopeFactory scopeFactory) =>
        new(scopeFactory, NullLogger<MentionDispatchHandler>.Instance);

    public static CloudEvent BuildEvent(
        string type,
        string projectId,
        int issueNumber,
        string eventId,
        string? workflowRunId = null)
    {
        var extensions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [EventCatalog.Lineage.ProjectId] = projectId,
            [EventCatalog.Lineage.Issue] = issueNumber.ToString(),
        };
        if (!string.IsNullOrWhiteSpace(workflowRunId))
        {
            extensions[EventCatalog.Lineage.WorkflowRunId] = workflowRunId;
        }
        return new CloudEvent(
            id: eventId,
            source: new Uri($"/mohist/projects/{projectId}/issues/{issueNumber}", UriKind.Relative),
            type: type,
            time: FixedEventTime,
            data: null,
            extensions: extensions);
    }

    public static CloudEvent BuildEventWithoutIssue(string type, string projectId, string eventId)
    {
        var extensions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [EventCatalog.Lineage.ProjectId] = projectId,
        };
        return new CloudEvent(
            id: eventId,
            source: new Uri($"/mohist/projects/{projectId}", UriKind.Relative),
            type: type,
            time: FixedEventTime,
            data: null,
            extensions: extensions);
    }

    public static async Task SeedAgentAsync(
        TestSqliteDatabase database,
        string projectId,
        string agentId,
        string status = AgentStatus.Active)
    {
        await using var db = database.CreateContext();
        db.Agents.Add(new AgentRow
        {
            Id = agentId,
            State = System.Text.Json.JsonSerializer.Serialize(
                new AgentDomain
                {
                    Id = agentId,
                    ProjectId = projectId,
                    Name = agentId,
                    Status = status,
                },
                Mohist.Server.Infrastructure.JSON.Options),
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Seeds an Agent row with an explicit name distinct from its id
    /// (the existing <see cref="SeedAgentAsync(TestSqliteDatabase,string,string,string)"/>
    /// overload sets <c>Name = Id</c>). Mention specs need a name that does
    /// not colliding with the id, so <c>@supervisor</c> resolves to an Agent
    /// whose id is e.g. <c>agent_supervisor</c> — mirroring how the
    /// production Agent-create path stores them.
    /// </summary>
    public static async Task SeedNamedAgentAsync(
        TestSqliteDatabase database,
        string projectId,
        string agentId,
        string name,
        string status = AgentStatus.Active)
    {
        await using var db = database.CreateContext();
        db.Agents.Add(new AgentRow
        {
            Id = agentId,
            State = System.Text.Json.JsonSerializer.Serialize(
                new AgentDomain
                {
                    Id = agentId,
                    ProjectId = projectId,
                    Name = name,
                    Status = status,
                },
                Mohist.Server.Infrastructure.JSON.Options),
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Builds a <c>com.mohist.issue.comment-added</c> CloudEvent with the
    /// lineage extensions and JSON payload shape the production
    /// <see cref="IssueGrain.AddCommentAsync"/> emits. The handler reads
    /// <c>commentId</c>/<c>author</c>/<c>body</c> out of <c>data</c> and the
    /// project / issue / epic out of the lineage extensions, so the builder
    /// must populate both exactly like the producer.
    /// </summary>
    public static CloudEvent BuildCommentAddedEvent(
        string projectId,
        int issueNumber,
        string eventId,
        string commentId,
        string author,
        string body,
        int? epicNumber = null)
    {
        var extensions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [EventCatalog.Lineage.ProjectId] = projectId,
            [EventCatalog.Lineage.Issue] = issueNumber.ToString(),
        };
        if (epicNumber is > 0)
        {
            extensions[EventCatalog.Lineage.Epic] = epicNumber.Value.ToString();
        }

        var payload = System.Text.Json.JsonSerializer.SerializeToElement(
            new { commentId, author, body },
            Mohist.Server.Infrastructure.JSON.Options);

        return new CloudEvent(
            id: eventId,
            source: new Uri($"/mohist/projects/{projectId}/issues/{issueNumber}", UriKind.Relative),
            type: EventCatalog.ReverseDns.IssueCommentAdded,
            time: FixedEventTime,
            data: payload,
            subject: issueNumber.ToString(),
            extensions: extensions);
    }

    public static async Task SeedWorkflowRunAsync(
        TestSqliteDatabase database,
        string workflowRunId,
        string projectId,
        int issueNumber,
        string workspacePath = "/mohist-tests/runner/test-workspace")
    {
        var run = new WorkflowRun
        {
            Id = workflowRunId,
            Metadata = new WorkflowRunMetadata(
                Name: null,
                CreatedAt: FixedEventTime,
                ProjectId: projectId,
                IssueNumber: issueNumber),
            Stages = new List<StageRun>(),
            Status = WorkflowRunStatus.Running,
            Workspace = new WorkspaceIdentity(Path: workspacePath),
        };
        await using var db = database.CreateContext();
        db.WorkflowRuns.Add(new WorkflowRunRow
        {
            WorkflowRunId = workflowRunId,
            State = Mohist.Server.Infrastructure.JSON.Serialize(run),
        });
        await db.SaveChangesAsync();
    }

    public static async Task SeedIssueWithRunAsync(
        TestSqliteDatabase database,
        string projectId,
        int issueNumber,
        string workflowRunId)
    {
        await using var db = database.CreateContext();
        var issue = new DomainIssue
        {
            ProjectId = projectId,
            Number = issueNumber,
            Title = $"Issue #{issueNumber}",
            Labels = new Dictionary<string, string>(StringComparer.Ordinal),
            Priority = "p2",
            WorkflowRunId = workflowRunId,
        };
        db.Issues.Add(new IssueRow
        {
            ProjectId = projectId,
            Number = issueNumber,
            State = IssueStore.Serialize(issue),
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Inserts a <see cref="WatchEntryRow"/> directly, bypassing the
    /// user-facing <see cref="WatchEntryStore"/> active-Agent validation.
    /// The dispatch handler's read path must not require the agent to
    /// be active (the launch gate is the active check inside the
    /// dispatch pass itself), so tests that exercise the gate need a
    /// way to seed an entry that the validation would otherwise reject.
    /// </summary>
    public static async Task SeedWatchEntryRawAsync(
        TestSqliteDatabase database,
        string projectId,
        int issueNumber,
        string agentId,
        string state)
    {
        await using var db = database.CreateContext();
        var existing = await db.WatchEntries.FirstOrDefaultAsync(entry =>
            entry.ProjectId == projectId
            && entry.IssueNumber == issueNumber
            && entry.AgentId == agentId);
        var now = FixedEventTime;
        if (existing is null)
        {
            db.WatchEntries.Add(new Mohist.Server.Infrastructure.Data.Agent.WatchEntryRow
            {
                ProjectId = projectId,
                IssueNumber = issueNumber,
                AgentId = agentId,
                State = state,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        else
        {
            existing.State = state;
            existing.UpdatedAt = now;
        }
        await db.SaveChangesAsync();
    }

    private sealed class DispatchScopeFactory : IServiceScopeFactory
    {
        private readonly TestSqliteDatabase _database;
        private readonly RecordingAgentLauncher _launcher;
        private readonly Action<IServiceCollection>? _configure;

        public DispatchScopeFactory(TestSqliteDatabase database, RecordingAgentLauncher launcher, Action<IServiceCollection>? configure)
        {
            _database = database;
            _launcher = launcher;
            _configure = configure;
        }

        public IServiceScope CreateScope() => new DispatchScope(_database, _launcher, _configure);

        private sealed class DispatchScope : IServiceScope
        {
            public DispatchScope(TestSqliteDatabase database, RecordingAgentLauncher launcher, Action<IServiceCollection>? configure)
            {
                ServiceProvider = BuildProvider(database, launcher, configure);
            }

            public IServiceProvider ServiceProvider { get; }

            public void Dispose() { }

            private static IServiceProvider BuildProvider(
                TestSqliteDatabase database,
                RecordingAgentLauncher launcher,
                Action<IServiceCollection>? configure)
            {
                var services = new ServiceCollection();
                var factory = new TestDbContextFactory(database.Options);
                services.AddSingleton<IDbContextFactory<MohistDbContext>>(factory);
                services.AddSingleton<TimeProvider>(new FakeTimeProvider(FixedEventTime));
                services.AddSingleton<IAgentLauncher>(launcher);
                services.AddSingleton<RecordingAgentLauncher>(launcher);
                services.AddSingleton<IGrainFactory, NullDispatchGrainFactory>();
                services.AddScoped<RoutingRuleStore>();
                services.AddScoped<WatchEntryStore>();
                services.AddScoped<AgentQuerier>();
                services.AddScoped<RoutedAgentLaunchContextResolver>();
                services.AddScoped<WorkflowRunQuerier>();
                services.AddSingleton<IActionCatalogSource>(NullActionCatalogSource.Instance);
                services.AddScoped<RoutingTableEvaluator>();
                configure?.Invoke(services);
                return services.BuildServiceProvider();
            }
        }
    }

    /// <summary>
    /// Minimal <see cref="IGrainFactory"/> stand-in. The dispatch
    /// handler resolves <see cref="Mohist.Server.Agent.Grains.IAgentJobGrain"/>
    /// via <see cref="IGrainFactory"/> only inside the preflight-failure
    /// path; routing- and watch-launch paths must not reach the grain
    /// because the fake launcher captures the plan and returns the
    /// executable outcome.
    /// </summary>
    private sealed class NullDispatchGrainFactory : IGrainFactory
    {
        public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, string grainPrimaryKey) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey, string keyExtension) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey, string keyExtension) => throw new NotSupportedException();
        public IAddressable GetGrain(GrainId grainId) => throw new NotSupportedException();
        public IAddressable GetGrain(GrainId grainId, GrainInterfaceType interfaceType) => throw new NotSupportedException();
        public IAddressable GetGrain(Type interfaceType, IdSpan grainKey, string grainClassNamePrefix) => throw new NotSupportedException();
        public IAddressable GetGrain(Type interfaceType, IdSpan grainKey) => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(string primaryKey, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithStringKey =>
            throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithIntegerKey =>
            throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithGuidKey =>
            throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string keyExtension, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithGuidCompoundKey =>
            throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string keyExtension, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithIntegerCompoundKey =>
            throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(GrainId grainId) where TGrainInterface : IAddressable =>
            throw new NotSupportedException();
        public TGrainObserverInterface CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj) where TGrainObserverInterface : IGrainObserver =>
            throw new NotSupportedException();
        public void DeleteObjectReference<TGrainObserverInterface>(IGrainObserver obj) where TGrainObserverInterface : IGrainObserver =>
            throw new NotSupportedException();
    }
}

/// <summary>
/// Records every <see cref="IAgentLauncher.LaunchRoutedAsync"/> call so
/// the spec can assert the dispatch handler's behavior end-to-end. The
/// fake pretends the launch succeeded with the canonical plan, so the
/// grain's first-writer semantics (issue-449) are not exercised here —
/// they are out of scope for this spec suite. Replays of the same
/// <c>(projectId, eventId, ruleId)</c> deliberately return a fresh
/// <see cref="RoutedAgentLaunchOutcome"/> with a new
/// <c>SessionId</c>/<c>JobKey</c> so the spec's "rule+watch single
/// launch" assertion stays scoped to within-delivery dedup (D7).
/// </summary>
public sealed class RecordingAgentLauncher : IAgentLauncher
{
    private readonly ConcurrentBag<RecordedRoutedLaunch> _routedLaunches = [];
    private readonly ConcurrentBag<RecordedMentionLaunch> _mentionLaunches = [];

    public IReadOnlyList<RecordedRoutedLaunch> RoutedLaunches =>
        _routedLaunches.OrderBy(entry => entry.Sequence).ToArray();

    public int RoutedLaunchCount => _routedLaunches.Count;

    public IReadOnlyList<RecordedMentionLaunch> MentionLaunches =>
        _mentionLaunches.OrderBy(entry => entry.Sequence).ToArray();

    public int MentionLaunchCount => _mentionLaunches.Count;

    public Task<RoutedAgentLaunchOutcome> LaunchRoutedAsync(
        AgentInfo agent,
        string prompt,
        RoutedExecutionContext executionContext,
        CloudEvent triggeringEvent,
        string ruleId,
        CancellationToken ct = default)
    {
        var sequence = _routedLaunches.Count;
        var outcome = new RoutedAgentLaunchOutcome(
            SessionId: $"session-{sequence}",
            JobKey: $"job-{sequence}",
            AgentId: agent.Id,
            AgentName: agent.Name,
            Disposition: RoutedLaunchDisposition.Executable);
        _routedLaunches.Add(new RecordedRoutedLaunch(
            Sequence: sequence,
            AgentId: agent.Id,
            AgentName: agent.Name,
            RuleId: ruleId,
            Prompt: prompt,
            EventType: triggeringEvent.Type,
            EventId: triggeringEvent.Id,
            ProjectId: executionContext.ProjectId,
            IssueNumber: executionContext.IssueNumber,
            WorkspacePath: executionContext.WorkspacePath,
            Disposition: outcome.Disposition));
        return Task.FromResult(outcome);
    }

    public Task<AgentLaunchResult> LaunchAsync(
        AgentInfo agent,
        string prompt,
        AgentLaunchContext context,
        IReadOnlyDictionary<string, string>? triggerLabels = null,
        CancellationToken ct = default) =>
        throw new NotSupportedException("RecordingAgentLauncher captures routed launches only.");

    public Task<AgentLaunchResult> LaunchMentionAsync(
        AgentInfo agent,
        string prompt,
        AgentLaunchContext context,
        string commentId,
        string triggeringEventId,
        CancellationToken ct = default)
    {
        var sequence = _mentionLaunches.Count;
        _mentionLaunches.Add(new RecordedMentionLaunch(
            Sequence: sequence,
            AgentId: agent.Id,
            AgentName: agent.Name,
            Prompt: prompt,
            CommentId: commentId,
            TriggeringEventId: triggeringEventId,
            ProjectId: context.ProjectId,
            IssueNumber: context.IssueNumber,
            EpicNumber: context.EpicNumber));
        return Task.FromResult(new AgentLaunchResult(
            SessionId: $"mention-session-{sequence}",
            JobKey: $"mention-job-{sequence}",
            InputId: string.Empty,
            TurnId: string.Empty,
            AgentId: agent.Id,
            AgentName: agent.Name));
    }

    public Task<AgentLaunchResult> LaunchIdempotentAsync(
        AgentInfo agent,
        string prompt,
        AgentLaunchContext context,
        string idempotencyKey,
        AgentLaunchCoordinatorRequest request,
        IReadOnlyList<AgentSessionInputAttachmentDescriptor>? attachments = null,
        string? preMintedSessionId = null,
        string? preMintedInputId = null,
        string? preMintedTurnId = null,
        CancellationToken ct = default) =>
        throw new NotSupportedException("RecordingAgentLauncher does not exercise the manual launch path.");

    public Task<AgentLaunchResult> LaunchSubagentAsync(
        string projectId,
        string parentSessionId,
        string targetAgentRef,
        string prompt,
        string idempotencyKey,
        CancellationToken ct = default) =>
        throw new NotSupportedException("RecordingAgentLauncher does not exercise the subagent launch path.");

    public Task<AgentLaunchResult> LaunchConnectionAsync(
        AgentInfo agent,
        string prompt,
        ConnectionLaunchOrigin origin,
        AgentStartupContext? startupContext = null,
        IReadOnlyList<AgentSessionInputAttachmentDescriptor>? attachments = null,
        IReadOnlyList<string>? attachmentIds = null,
        string? preMintedSessionId = null,
        string? preMintedInputId = null,
        string? preMintedTurnId = null,
        CancellationToken ct = default) =>
        throw new NotSupportedException("RecordingAgentLauncher does not exercise the connection launch path.");

    public Task<AgentLaunchResult?> ResumeIdempotentAsync(
        string projectId,
        string idempotencyKey,
        AgentLaunchCoordinatorRequest request,
        CancellationToken ct = default) =>
        throw new NotSupportedException("RecordingAgentLauncher does not exercise the manual launch path.");
}

public sealed record RecordedMentionLaunch(
    int Sequence,
    string AgentId,
    string AgentName,
    string Prompt,
    string CommentId,
    string TriggeringEventId,
    string ProjectId,
    int? IssueNumber,
    int? EpicNumber);

public sealed record RecordedRoutedLaunch(
    int Sequence,
    string AgentId,
    string AgentName,
    string RuleId,
    string Prompt,
    string EventType,
    string EventId,
    string ProjectId,
    int? IssueNumber,
    string? WorkspacePath,
    RoutedLaunchDisposition Disposition);
