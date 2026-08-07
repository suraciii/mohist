using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.AgentOps.Services;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.AgentOps;

/// <summary>
/// Calculation specs for <see cref="ProjectEventFeedAssembler"/>, the service
/// behind <c>GET /api/projects/&#123;projectRef&#125;/events</c>. These assert the
/// assembler's read semantics (default/explicit limit, cap, cross-aggregate
/// descending order with stable tie-break, sub-second precision, project
/// isolation, envelope-over-stored-payload context priority, activity-safe
/// payload projection, attention/failure filters, bounded history window,
/// repository-change bucket, read-only) without an HTTP round-trip.
/// The route-level contract (404/400/one success-path shape) stays in
/// <c>ProjectEventsApiSpecs</c>.
/// </summary>
[Collection("MohistDb")]
public class ProjectEventFeedAssemblerSpecs
{
    private readonly MohistDbFixture _fixture;
    private readonly ProjectEventSeedSupport _seeds;

    public ProjectEventFeedAssemblerSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
        _seeds = new ProjectEventSeedSupport(fixture.Services);
    }

    private ProjectEventFeedAssembler CreateAssembler() =>
        _fixture.Services.GetRequiredService<ProjectEventFeedAssembler>();

    private static ProjectEventFilter? Filter(string? types, bool attentionOnly = false) =>
        ProjectEventFilter.TryCreate(types, attentionOnly, out var filter) ? filter : null;

    [Fact]
    public async Task ListAsync_DefaultLimit_CapsAtTwoHundredDescending()
    {
        var projectId = UniqueProject();
        await _seeds.SeedIssueAsync(projectId, 1);

        var t0 = ProjectEventSeedSupport.FixedTime.AddHours(-10);
        for (var i = 0; i < 205; i++)
        {
            await _seeds.AppendIssueEventAsync(projectId, 1, $"test.event-{i}",
                time: t0.AddMinutes(i), subject: "1");
        }

        var events = await CreateAssembler().ListAsync(projectId);

        Assert.Equal(200, events.Count);
        Assert.Equal("test.event-204", events[0].Type);
        Assert.Equal("test.event-5", events[^1].Type);
    }

    [Fact]
    public async Task ListAsync_ExplicitLimit_CapsReturnedRows()
    {
        var projectId = UniqueProject();
        await _seeds.SeedIssueAsync(projectId, 1);

        var t0 = ProjectEventSeedSupport.FixedTime.AddHours(-2);
        for (var i = 0; i < 5; i++)
        {
            await _seeds.AppendIssueEventAsync(projectId, 1, $"test.event-{i}",
                time: t0.AddMinutes(i), subject: "1");
        }

        var events = await CreateAssembler().ListAsync(projectId, limit: 2);

        Assert.Equal(2, events.Count);
        Assert.Equal("test.event-4", events[0].Type);
        Assert.Equal("test.event-3", events[1].Type);
    }

    [Fact]
    public async Task ListAsync_LimitZero_FallsBackToDefaultTwoHundred()
    {
        var projectId = UniqueProject();
        await _seeds.SeedIssueAsync(projectId, 1);
        await _seeds.SeedIssueEventHistoryAsync(projectId, 1, 1_000);

        var events = await CreateAssembler().ListAsync(projectId, limit: 0);

        Assert.Equal(200, events.Count);
    }

    [Fact]
    public async Task ListAsync_RequestBeyondHistory_ReturnsEverythingUpToStoredRows()
    {
        var projectId = UniqueProject();
        await _seeds.SeedIssueAsync(projectId, 1);
        await _seeds.SeedIssueEventHistoryAsync(projectId, 1, 1_000);

        var firstTwo = await CreateAssembler().ListAsync(projectId, limit: 2);
        var all = await CreateAssembler().ListAsync(projectId, limit: 1001);

        Assert.Collection(firstTwo,
            entry => Assert.Equal(1_000, entry.Id),
            entry => Assert.Equal(999, entry.Id));
        Assert.Equal(1_000, all.Count);
    }

    [Fact]
    public async Task ListAsync_MergesAggregatesInDescendingTimeOrder()
    {
        var projectId = UniqueProject();
        var workflowRunId = UniqueWorkflowRun();
        var sessionId = UniqueSession();

        await _seeds.SeedIssueAsync(projectId, 1);
        await _seeds.SeedWorkflowRunAsync(projectId, workflowRunId, 1);
        await _seeds.SeedAgentSessionAsync(projectId, sessionId);
        await _seeds.SeedEpicAsync(projectId, 1);

        var t0 = ProjectEventSeedSupport.FixedTime.AddMinutes(-10);
        await _seeds.AppendIssueEventAsync(projectId, 1, "com.mohist.issue.created", time: t0, subject: "1");
        await _seeds.AppendWorkflowEventAsync(workflowRunId, projectId, 1, "com.mohist.workflow.stage.started", time: t0.AddMinutes(1), subject: null);
        await _seeds.AppendAgentSessionEventAsync(sessionId, projectId, "com.mohist.agent-session.runtime-bound", time: t0.AddMinutes(2), subject: sessionId);
        await _seeds.AppendEpicEventAsync(projectId, 1, "com.mohist.epic.created", time: t0.AddMinutes(3), subject: "1");

        var events = await CreateAssembler().ListAsync(projectId);

        Assert.Equal(4, events.Count);
        Assert.DoesNotContain(events, entry => entry.Type == "com.mohist.epic.created");

        for (var i = 1; i < events.Count; i++)
            Assert.True(events[i - 1].Time >= events[i].Time,
                $"Expected descending order but {events[i - 1].Time:o} < {events[i].Time:o}");
    }

    [Fact]
    public async Task ListAsync_LimitOne_UsesStableTieBreakAcrossAggregates()
    {
        var projectId = UniqueProject();
        var workflowRunId = UniqueWorkflowRun();
        await _seeds.SeedIssueAsync(projectId, 1);
        await _seeds.SeedWorkflowRunAsync(projectId, workflowRunId, 1);

        await _seeds.AppendIssueEventAsync(projectId, 1, "com.mohist.issue.created", time: ProjectEventSeedSupport.FixedTime, subject: "1");
        await _seeds.AppendWorkflowEventAsync(workflowRunId, projectId, 1, "com.mohist.workflow.stage.started", time: ProjectEventSeedSupport.FixedTime);

        var first = await CreateAssembler().ListAsync(projectId, limit: 1);
        var second = await CreateAssembler().ListAsync(projectId, limit: 1);

        Assert.Equal("com.mohist.issue.created", Assert.Single(first).Type);
        Assert.Equal(first[0].EnvelopeId, Assert.Single(second).EnvelopeId);
    }

    [Fact]
    public async Task ListAsync_SubSecondTimes_AreOrderedByFractionalPrecision()
    {
        var projectId = UniqueProject();
        await _seeds.SeedIssueAsync(projectId, 1);

        var baseTime = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        await _seeds.AppendIssueEventAsync(projectId, 1, "com.mohist.issue.created", time: baseTime.AddMilliseconds(900), subject: "late");
        await _seeds.AppendIssueEventAsync(projectId, 1, "com.mohist.issue.work-started", time: baseTime.AddMilliseconds(100), subject: "early");
        await _seeds.AppendIssueEventAsync(projectId, 1, "com.mohist.issue.completed", time: baseTime.AddMilliseconds(500), subject: "mid");

        var events = await CreateAssembler().ListAsync(projectId, limit: 3);

        Assert.Collection(events,
            entry => Assert.Equal("late", entry.Subject),
            entry => Assert.Equal("mid", entry.Subject),
            entry => Assert.Equal("early", entry.Subject));
    }

    [Fact]
    public async Task ListAsync_DoesNotLeakIssueEventsFromOtherProjects()
    {
        var projectA = UniqueProject();
        var projectB = UniqueProject();
        await _seeds.SeedIssueAsync(projectA, 1);
        await _seeds.SeedIssueAsync(projectB, 1);

        await _seeds.AppendIssueEventAsync(projectA, 1, "com.mohist.issue.created", subject: "1");
        await _seeds.AppendIssueEventAsync(projectB, 1, "com.mohist.issue.created", subject: "1");

        var eventsA = await CreateAssembler().ListAsync(projectA);
        var eventsB = await CreateAssembler().ListAsync(projectB);

        Assert.Single(eventsA);
        Assert.Equal("1", eventsA[0].SourceAggregateId);
        Assert.Single(eventsB);
        Assert.Equal("1", eventsB[0].SourceAggregateId);
    }

    [Fact]
    public async Task ListAsync_DoesNotLeakAgentSessionsFromOtherProjects()
    {
        var projectA = UniqueProject();
        var projectB = UniqueProject();
        var sessionA = UniqueSession();
        var sessionB = UniqueSession();
        await _seeds.SeedAgentSessionAsync(projectA, sessionA);
        await _seeds.SeedAgentSessionAsync(projectB, sessionB);

        await _seeds.AppendAgentSessionEventAsync(sessionA, projectA, "com.mohist.agent-session.runtime-bound", subject: sessionA);
        await _seeds.AppendAgentSessionEventAsync(sessionB, projectB, "com.mohist.agent-session.runtime-bound", subject: sessionB);

        var eventsA = await CreateAssembler().ListAsync(projectA);
        var eventsB = await CreateAssembler().ListAsync(projectB);

        Assert.All(eventsA, entry => Assert.Equal(sessionA, entry.SourceAggregateId));
        Assert.All(eventsB, entry => Assert.Equal(sessionB, entry.SourceAggregateId));
        Assert.DoesNotContain(eventsB, entry => entry.SourceAggregateId == sessionA);
    }

    [Fact]
    public async Task ListAsync_ExcludesEpicsFromOtherAggregates()
    {
        var projectId = UniqueProject();
        await _seeds.SeedEpicAsync(projectId, 1);
        await _seeds.SeedIssueAsync(projectId, 1);

        await _seeds.AppendEpicEventAsync(projectId, 1, "com.mohist.epic.created", subject: "1");
        await _seeds.AppendIssueEventAsync(projectId, 1, "com.mohist.issue.created", subject: "1");

        var events = await CreateAssembler().ListAsync(projectId);

        Assert.DoesNotContain(events, entry => entry.Type == "com.mohist.epic.created");
        Assert.Contains(events, entry => entry.Type == "com.mohist.issue.created");
    }

    [Fact]
    public async Task ListAsync_WorkflowIssueNumber_PrefersStoredWorkflowRunMetadata()
    {
        var projectId = UniqueProject();
        var workflowRunId = UniqueWorkflowRun();
        await _seeds.SeedIssueAsync(projectId, 42);

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IWorkflowRunStore>();
            await store.SaveAsync(new Mohist.Server.Workflow.Domain.Run.WorkflowRun
            {
                Id = workflowRunId,
                Metadata = new Mohist.Server.Workflow.Domain.Run.WorkflowRunMetadata(
                    Name: null,
                    CreatedAt: ProjectEventSeedSupport.FixedTime,
                    ProjectId: projectId,
                    IssueNumber: 42),
                Stages = [],
            }, [new Mohist.Server.Workflow.Domain.Run.WorkflowRunFailed("failed")]);
        }

        var events = await CreateAssembler().ListAsync(projectId);

        var workflow = Assert.Single(events, entry => entry.Type == "com.mohist.workflow.run.failed");
        Assert.Equal(42, workflow.IssueNumber);
        Assert.Equal(workflowRunId, workflow.SourceAggregateId);
    }

    [Fact]
    public async Task ListAsync_WorkflowIssueNumber_PrefersEnvelopeOverPayload()
    {
        var projectId = UniqueProject();
        var workflowRunId = UniqueWorkflowRun();
        await _seeds.SeedIssueAsync(projectId, 42);
        await _seeds.SeedWorkflowRunAsync(projectId, workflowRunId, 42);

        await _seeds.AppendWorkflowEventAsync(
            workflowRunId,
            projectId,
            issueNumber: 42,
            type: EventCatalog.ReverseDns.WorkflowRunFailed,
            data: new { issueNumber = 42 },
            envelopeIssueNumber: 99);

        var events = await CreateAssembler().ListAsync(projectId);

        var workflow = Assert.Single(events, entry => entry.Type == EventCatalog.ReverseDns.WorkflowRunFailed);
        Assert.Equal(99, workflow.IssueNumber);
        Assert.Equal(42, workflow.Data.GetProperty("issueNumber").GetInt32());
    }

    [Fact]
    public async Task ListAsync_SessionContext_PrefersEnvelopeOverStoredMetadataAndPayload()
    {
        var projectId = UniqueProject();
        var sessionId = UniqueSession();
        await _seeds.SeedAgentSessionAsync(projectId, sessionId);

        await _seeds.AppendAgentSessionEventAsync(
            sessionId,
            projectId,
            "com.mohist.agent-session.runtime-bound",
            data: new { issueNumber = 1, epicNumber = 7 },
            envelopeIssueNumber: 99,
            envelopeEpicNumber: 8);

        var events = await CreateAssembler().ListAsync(projectId);

        var entry = Assert.Single(events, item => item.Type == "com.mohist.agent-session.runtime-bound");
        Assert.Equal(99, entry.IssueNumber);
        Assert.Equal(8, entry.EpicNumber);
        Assert.Equal(1, entry.Data.GetProperty("issueNumber").GetInt32());
    }

    [Fact]
    public async Task ListAsync_SessionContext_LeavesIssueEpicNullWhenEnvelopeAbsent()
    {
        var projectId = UniqueProject();
        var sessionId = UniqueSession();
        await _seeds.SeedAgentSessionAsync(projectId, sessionId);

        await _seeds.AppendAgentSessionEventAsync(
            sessionId,
            projectId,
            "com.mohist.agent-session.runtime-bound",
            includeIssueContext: false);

        var events = await CreateAssembler().ListAsync(projectId);

        var entry = Assert.Single(events, item => item.Type == "com.mohist.agent-session.runtime-bound");
        Assert.Null(entry.IssueNumber);
        Assert.Null(entry.EpicNumber);
    }

    [Fact]
    public async Task ListAsync_ProjectsPersistedSessionLifecycleWithHistoricalContext()
    {
        var projectId = UniqueProject();
        var sessionId = UniqueSession();
        await _seeds.SeedAgentSessionAsync(projectId, sessionId);
        await _seeds.AppendSessionActivityFactAsync(sessionId, ProjectEventSeedSupport.FixedTime.AddMinutes(1));

        var events = await CreateAssembler().ListAsync(projectId);

        var opened = Assert.Single(events, entry => entry.Type == "coder_session_started");
        Assert.Equal("workflow", opened.SessionSourceKind);
        Assert.Equal(1, opened.IssueNumber);
        Assert.Equal(7, opened.EpicNumber);
        Assert.Equal("wf-1", opened.WorkflowRunId);
        Assert.Equal("runner-1", opened.RunnerId);

        var activity = Assert.Single(events, entry => entry.Type == "session.activity");
        Assert.Equal("failed", activity.Data.GetProperty("status").GetString());
        Assert.Equal("workflow", activity.SessionSourceKind);
        Assert.Equal(1, activity.IssueNumber);
        Assert.Equal(7, activity.EpicNumber);
        Assert.Equal("runner-1", activity.RunnerId);
    }

    [Fact]
    public async Task ListAsync_ProjectsCompletedSessionActivity()
    {
        var projectId = UniqueProject();
        var sessionId = UniqueSession();
        await _seeds.SeedAgentSessionAsync(projectId, sessionId);
        await _seeds.AppendSessionActivityFactAsync(
            sessionId,
            ProjectEventSeedSupport.FixedTime.AddMinutes(1),
            status: "completed",
            failureReason: null);

        var events = await CreateAssembler().ListAsync(projectId);

        var activity = Assert.Single(events, entry => entry.Type == "session.activity");
        Assert.Equal("completed", activity.Data.GetProperty("status").GetString());
        Assert.Equal("agent-session", activity.SourceAggregateKind);
        Assert.Equal(sessionId, activity.SourceAggregateId);
        Assert.Equal(sessionId, activity.Subject);
    }

    [Fact]
    public async Task ListAsync_AttentionFilter_FindsOlderWorkflowFailureBeyondRoutineWindow()
    {
        var projectId = UniqueProject();
        var workflowRunId = UniqueWorkflowRun();
        await _seeds.SeedIssueAsync(projectId, 1);
        await _seeds.SeedWorkflowRunAsync(projectId, workflowRunId, 1);

        await _seeds.AppendWorkflowEventAsync(workflowRunId, projectId, 1, "com.mohist.workflow.stage.failed", time: ProjectEventSeedSupport.FixedTime.AddHours(-2));
        for (var i = 0; i < 205; i++)
        {
            await _seeds.AppendWorkflowEventAsync(workflowRunId, projectId, 1, "com.mohist.workflow.stage.started", time: ProjectEventSeedSupport.FixedTime.AddMinutes(i));
        }

        var events = await CreateAssembler().ListAsync(projectId, filter: Filter("failure", attentionOnly: true));

        var failure = Assert.Single(events);
        Assert.Equal("com.mohist.workflow.stage.failed", failure.Type);
    }

    [Fact]
    public async Task ListAsync_AttentionFilter_FindsOlderSessionStatusFailureBeyondRoutineWindow()
    {
        var projectId = UniqueProject();
        var sessionId = UniqueSession();
        await _seeds.SeedAgentSessionAsync(projectId, sessionId);

        await _seeds.AppendAgentSessionEventAsync(sessionId, projectId, "coder_session_status_changed",
            time: ProjectEventSeedSupport.FixedTime.AddHours(-2),
            data: new { status = "failed" });
        for (var i = 0; i < 205; i++)
        {
            await _seeds.AppendAgentSessionEventAsync(sessionId, projectId, "coder_session_status_changed",
                time: ProjectEventSeedSupport.FixedTime.AddMinutes(i),
                data: new { status = "active" });
        }

        var events = await CreateAssembler().ListAsync(projectId, filter: Filter("failure", attentionOnly: true));

        var failure = Assert.Single(events);
        Assert.Equal("coder_session_status_changed", failure.Type);
        Assert.Equal("failed", failure.Data.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ListAsync_RepositoryChangedAppearsInIssueStateBucket()
    {
        var projectId = UniqueProject();
        await _seeds.SeedIssueAsync(projectId, 11);

        await _seeds.AppendIssueEventAsync(projectId, 11, "com.mohist.issue.repository-changed",
            time: ProjectEventSeedSupport.FixedTime,
            subject: "11",
            data: new
            {
                oldRepositoryRef = "main",
                newRepositoryRef = "web",
                commandId = "cmd-1",
                expectedRevision = 1L,
                appliedRevision = 2L,
            });

        var events = await CreateAssembler().ListAsync(projectId, filter: Filter("issue-state"));

        var entry = Assert.Single(events);
        Assert.Equal("com.mohist.issue.repository-changed", entry.Type);
        Assert.Equal("11", entry.Subject);
        Assert.Equal("1.0", entry.SpecVersion);
        Assert.Equal("issue", entry.SourceAggregateKind);
    }

    [Fact]
    public async Task ListAsync_DoesNotCreateAnyNewEvents()
    {
        var projectId = UniqueProject();
        await _seeds.SeedIssueAsync(projectId, 1);

        await _seeds.AppendIssueEventAsync(projectId, 1, "com.mohist.issue.created",
            time: ProjectEventSeedSupport.FixedTime.AddMinutes(-5),
            subject: "1");

        await using var db = await _fixture.Services
            .GetRequiredService<IDbContextFactory<MohistDbContext>>()
            .CreateDbContextAsync();

        var projectEventMarker = $"\"projectid\":\"{projectId}\"";
        var issueCountBefore = await db.IssueEvents.AsNoTracking()
            .Where(row => row.ExtensionsJson.Contains(projectEventMarker))
            .CountAsync();
        var workflowCountBefore = await db.WorkflowRunEvents.AsNoTracking()
            .Where(row => row.ExtensionsJson.Contains(projectEventMarker))
            .CountAsync();
        var sessionCountBefore = await db.AgentSessionEvents.AsNoTracking()
            .Where(row => row.ExtensionsJson.Contains(projectEventMarker))
            .CountAsync();

        var events = await CreateAssembler().ListAsync(projectId);
        Assert.Single(events);

        Assert.Equal(issueCountBefore, await db.IssueEvents.AsNoTracking()
            .Where(row => row.ExtensionsJson.Contains(projectEventMarker))
            .CountAsync());
        Assert.Equal(workflowCountBefore, await db.WorkflowRunEvents.AsNoTracking()
            .Where(row => row.ExtensionsJson.Contains(projectEventMarker))
            .CountAsync());
        Assert.Equal(sessionCountBefore, await db.AgentSessionEvents.AsNoTracking()
            .Where(row => row.ExtensionsJson.Contains(projectEventMarker))
            .CountAsync());
    }

    [Fact]
    public async Task ListAsync_ForProjectWithNoEvents_ReturnsEmpty()
    {
        var projectId = UniqueProject();

        var events = await CreateAssembler().ListAsync(projectId);

        Assert.Empty(events);
    }

    private static string UniqueProject() => $"proj-{Guid.NewGuid():N}";
    private static string UniqueWorkflowRun() => $"wf_{Guid.NewGuid():N}";
    private static string UniqueSession() => $"agent_session_{Guid.NewGuid():N}";
}
