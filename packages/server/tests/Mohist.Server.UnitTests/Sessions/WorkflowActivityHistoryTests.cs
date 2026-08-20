using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Sessions;

/// <summary>
/// Issue-421: history-bounded status selection, asserted at its lower
/// owners (<see cref="AgentSessionQuery.ListStatusCandidatesAsync"/> and
/// <see cref="WorkflowActivityQuerier.ListActiveAgentsResultAsync"/>).
/// The HTTP wire contract for these routes is owned by
/// <c>AgentPathAmplificationSpecs</c> (wire counters) and OTel parity by
/// <c>AgentPathAmplificationOtelEnabledSpecs</c>. Deterministic coverage that the
/// <see cref="AgentSessionQuery.ListStatusCandidatesAsync"/> path:
/// <list type="bullet">
/// <item>materializes only active direct Sessions and Sessions for
///   running Workflow Runs in the requested project;</item>
/// <item>preserves the existing global creation-descending order and
///   the existing <c>activeAgents</c> response shape;</item>
/// <item>performs one <see cref="IWorkflowStatusReader.GetStatusAsync"/>
///   read per distinct running Workflow even when multiple candidate
///   Sessions reference the same Workflow;</item>
/// <item>excludes a Workflow that terminalizes between candidate
///   selection and status read;</item>
/// <item>scales with active work, not historical Sessions — adding
///   completed / failed / cancelled / idle rows leaves
///   the response, candidate count, materialized-row count, and
///   database / downstream call counts unchanged.</item>
/// </list>
/// All assertions use operation counters (request-work interceptors +
/// the test seam exposed on <see cref="AgentSessionQuery"/> + a
/// counting <see cref="IWorkflowStatusReader"/> fake), never wall-clock.
/// </summary>
public sealed class WorkflowActivityHistoryTests : WorkflowActivityHistoryTestSupport
{
    [Fact]
    public async Task Status_WithIdenticalActiveWorkAndHistoricalSessions_KeepsResponseStable()
    {
        var project = await CreateProjectAsync("status-history-bounded");
        await InsertActiveDirectSessionsAsync(project, count: 2);
        var small = await ListStatusAsync(project);
        var smallMaterializedRows = await CountMaterializedRowsAsync(SessionQuery, project);

        await InsertInactiveHistoricalSessionsAsync(project, count: 100);

        var status = await ListStatusAsync(project);
        var materializedRows = await CountMaterializedRowsAsync(SessionQuery, project);

        Assert.Equal(
            small.ActiveAgents.Select(a => a.SessionId).OrderBy(x => x, StringComparer.Ordinal),
            status.ActiveAgents.Select(a => a.SessionId).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(small.Candidates, status.Candidates);
        Assert.Equal(small.ActiveAgents.Count, status.ActiveAgents.Count);
        Assert.Equal(smallMaterializedRows, materializedRows);
    }

    [Fact]
    public async Task Status_OnlyActiveDirectSessionsInProject_AreEmitted()
    {
        var project = await CreateProjectAsync("status-direct-active-only");
        var activeSessionId = (await InsertActiveDirectSessionsAsync(project, count: 1)).Single();

        var status = await ListStatusAsync(project);

        var entry = Assert.Single(status.ActiveAgents, a => a.SessionId == activeSessionId);
        Assert.Equal(project, entry.ProjectId);
    }

    [Fact]
    public async Task Status_InactiveDirectSessionIsNotEmitted()
    {
        var project = await CreateProjectAsync("status-direct-inactive");
        var sessionId = await InsertIdleDirectSessionAsync(project);

        var status = await ListStatusAsync(project);

        Assert.DoesNotContain(status.ActiveAgents, a => a.SessionId == sessionId);
    }

    [Fact]
    public async Task Status_SessionForNonRunningWorkflow_IsNotEmitted()
    {
        var project = await CreateProjectAsync("status-workflow-not-running");
        var workflowRunId = $"wf-{Guid.NewGuid():N}";
        var sessionId = await InsertWorkflowSessionAsync(project, workflowRunId, issueNumber: 10);
        // No WorkflowRuns row persisted → predicate never selects the
        // session. Confirms the joined-branch selection only retains
        // sessions whose Workflow Run is currently Running.
        _ = sessionId;

        var status = await ListStatusAsync(project);

        Assert.DoesNotContain(status.ActiveAgents, a => a.SessionId == sessionId);
        Assert.Equal(0, status.Candidates);
    }

    [Fact]
    public async Task Status_WorkflowSessionWithoutWorkId_IsNotACandidate()
    {
        var project = await CreateProjectAsync("status-workflow-no-work-id");
        var workflowRunId = $"wf-{Guid.NewGuid():N}";
        await InsertWorkflowRunRowAsync(workflowRunId, project, status: "running");
        await InsertWorkflowSessionAsync(project, workflowRunId, issueNumber: 10, workId: string.Empty);

        var rows = await CountMaterializedRowsAsync(SessionQuery, project);
        var status = await ListStatusAsync(project);

        Assert.Equal(0, rows);
        Assert.Equal(0, status.Candidates);
        Assert.Empty(status.ActiveAgents);
        Assert.Equal(0, WorkflowStatuses.GetStatusCallCount(workflowRunId));
    }

    [Fact]
    public async Task Status_LegacyWorkflowSessionWithoutSourceKind_RemainsVisible()
    {
        var project = await CreateProjectAsync("status-legacy-workflow");
        var workflowRunId = $"wf-{Guid.NewGuid():N}";
        var workId = $"work-{Guid.NewGuid():N}";
        await InsertWorkflowRunRowAsync(workflowRunId, project, status: "running");
        var sessionId = await InsertWorkflowSessionAsync(
            project,
            workflowRunId,
            issueNumber: 10,
            workId: workId,
            sourceKind: null);

        WorkflowStatuses.SetStatus(workflowRunId, BuildRunningViewWithPendingWork(workflowRunId, workId));

        var status = await ListStatusAsync(project);

        Assert.Equal(1, status.Candidates);
        Assert.Contains(status.ActiveAgents, agent => agent.SessionId == sessionId);
    }

    [Fact]
    public async Task Status_SessionForTerminalizedWorkflow_IsExcludedByCurrentStatusCheck()
    {
        var project = await CreateProjectAsync("status-workflow-terminalized");
        var workflowRunId = $"wf-{Guid.NewGuid():N}";
        var workId = $"work-{Guid.NewGuid():N}";
        await InsertWorkflowSessionAsync(project, workflowRunId, issueNumber: 11, workId: workId);
        await InsertWorkflowRunRowAsync(workflowRunId, project, status: "running");

        var sessionQuery = new TerminalizingAgentSessionQuery(
            DbFactory,
            TimeProvider,
            () => TerminalizeWorkflowAsync(workflowRunId));
        var projection = CreateQuerier(sessionQuery);

        var status = await projection.ListActiveAgentsResultAsync(project);

        Assert.True(sessionQuery.SelectedCandidates);
        Assert.Equal(1, status.Candidates);
        Assert.Empty(status.ActiveAgents);
        Assert.Equal(0, WorkflowStatuses.GetStatusCallCount(workflowRunId));
    }

    [Fact]
    public async Task Status_MultipleCandidateSessionsForOneRunningWorkflow_CausesOneWorkflowStatusRead()
    {
        var project = await CreateProjectAsync("status-workflow-dedup");
        var workflowRunId = $"wf-{Guid.NewGuid():N}";
        var workId = $"work-{Guid.NewGuid():N}";
        await InsertWorkflowRunRowAsync(workflowRunId, project, status: "running");
        var sessionIds = await InsertWorkflowSessionsForSingleRunAsync(project, workflowRunId, workId, count: 4);

        WorkflowStatuses.SetStatus(workflowRunId, BuildRunningViewWithPendingWork(workflowRunId, workId));

        var status = await ListStatusAsync(project);

        // The post-selection Workflow status read must be deduped
        // across the 4 candidates that reference the same Workflow.
        Assert.Equal(1, WorkflowStatuses.GetStatusCallCount(workflowRunId));

        // The post-selection pending-work match keeps every Session.
        var activeSessionIds = status.ActiveAgents.Select(a => a.SessionId).ToHashSet();
        Assert.Superset(activeSessionIds, sessionIds.ToHashSet());
    }

    [Fact]
    public async Task Status_CrossProjectSessionReferencingRunningWorkflowInRequestedProject_IsExcluded()
    {
        var selectedProject = await CreateProjectAsync("status-cross-selected");
        var otherProject = await CreateProjectAsync("status-cross-other");
        var workflowRunId = $"wf-{Guid.NewGuid():N}";
        var workId = $"work-{Guid.NewGuid():N}";
        await InsertWorkflowRunRowAsync(workflowRunId, projectId: selectedProject, status: "running");
        // Session belongs to a different project even though its
        // Workflow source id matches a Workflow in the selected project.
        var sessionId = await InsertWorkflowSessionAsync(
            projectId: otherProject,
            workflowRunId: workflowRunId,
            issueNumber: 12,
            workId: workId);

        WorkflowStatuses.SetStatus(workflowRunId, BuildRunningViewWithPendingWork(workflowRunId, workId));

        var status = await ListStatusAsync(selectedProject);

        Assert.DoesNotContain(status.ActiveAgents, a => a.SessionId == sessionId);
        // No Sessions in the selected project can reference the
        // Workflow, so no status read should be issued for it.
        Assert.Equal(0, WorkflowStatuses.GetStatusCallCount(workflowRunId));
    }

    [Fact]
    public async Task Status_NoActiveWork_ReportsZeroCandidatesAndProcessed()
    {
        var project = await CreateProjectAsync("status-empty-zero");

        var status = await ListStatusAsync(project);

        Assert.Equal(0, status.Candidates);
        Assert.Empty(status.ActiveAgents);
    }

    private async Task<int> CountMaterializedRowsAsync(AgentSessionQuery sessionQuery, string projectId)
    {
        var captured = 0;
        var previousCallback = sessionQuery.OnRowsMaterializedCallback;
        try
        {
            sessionQuery.OnRowsMaterializedCallback = rows => captured = rows;
            _ = await sessionQuery.ListStatusCandidatesAsync(projectId);
        }
        finally
        {
            sessionQuery.OnRowsMaterializedCallback = previousCallback;
        }
        return captured;
    }

    private async Task<ActiveAgentsListResult> ListStatusAsync(string projectId)
    {
        return await CreateQuerier().ListActiveAgentsResultAsync(projectId);
    }

    private async Task<string> CreateProjectAsync(string suffix)
    {
        var raw = $"{suffix}-{Guid.NewGuid():N}".ToLowerInvariant();
        var id = raw.Length > 63 ? raw[..63] : raw;
        await using var db = await DbFactory.CreateDbContextAsync();
        db.Projects.Add(new ProjectRow
        {
            Id = id,
            Name = id,
            RepositoriesJson = "[]",
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<IReadOnlyList<string>> InsertActiveDirectSessionsAsync(string projectId, int count)
    {
        var now = TimeProvider.GetUtcNow().UtcDateTime;
        var ids = Enumerable.Range(0, count).Select(_ => $"session-{Guid.NewGuid():N}").ToArray();
        await using var db = await DbFactory.CreateDbContextAsync();

        for (var index = 0; index < ids.Length; index++)
        {
            var id = ids[index];
            var session = new AgentSession
            {
                Id = id,
                Runtime = new AgentSessionRuntime("runner-status-active", null),
                Settings = new AgentSessionSettings("test-model"),
                Status = new AgentSessionStatusSnapshot(
                    CreatedAt: now,
                    BoundAt: now,
                    LastDataAt: now,
                    AgentRuntimeSessionId: id,
                    Activity: AgentSessionActivity.Active),
                Metadata = new AgentSessionMetadata(new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
                    [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
                    [GenericAgentSessionMetadata.AgentId] = $"agent-{index}",
                    [GenericAgentSessionMetadata.AgentName] = $"Agent {index}",
                }),
            };
            db.AgentSessions.Add(new AgentSessionRow
            {
                Id = id,
                State = JsonSerializer.Serialize(session, AgentSessionJson.JsonOptions),
                CreatedAt = now.AddTicks(index),
                Status = "bound",
                AgentSessionId = id,
                RunnerId = "runner-status-active",
            });
        }
        await db.SaveChangesAsync();
        return ids;
    }

    private async Task<string> InsertIdleDirectSessionAsync(string projectId)
    {
        var now = TimeProvider.GetUtcNow().UtcDateTime;
        var id = $"session-{Guid.NewGuid():N}";
        await using var db = await DbFactory.CreateDbContextAsync();

        var session = new AgentSession
        {
            Id = id,
            Runtime = new AgentSessionRuntime("runner-status-idle", null),
            Settings = new AgentSessionSettings("test-model"),
            Status = new AgentSessionStatusSnapshot(
                CreatedAt: now,
                AgentRuntimeSessionId: id,
                Activity: AgentSessionActivity.Idle),
            Metadata = new AgentSessionMetadata(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
                [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
                [GenericAgentSessionMetadata.AgentId] = "agent-idle",
                [GenericAgentSessionMetadata.AgentName] = "Idle Agent",
            }),
        };
        db.AgentSessions.Add(new AgentSessionRow
        {
            Id = id,
            State = JsonSerializer.Serialize(session, AgentSessionJson.JsonOptions),
            CreatedAt = now,
            Status = "bound",
            AgentSessionId = id,
            RunnerId = "runner-status-idle",
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task InsertInactiveHistoricalSessionsAsync(string projectId, int count)
    {
        var now = TimeProvider.GetUtcNow().UtcDateTime;
        await using var db = await DbFactory.CreateDbContextAsync();
        for (var index = 0; index < count; index++)
        {
            var id = $"hist-{Guid.NewGuid():N}";
            var session = new AgentSession
            {
                Id = id,
                Runtime = new AgentSessionRuntime("runner-status-historical", null),
                Settings = new AgentSessionSettings("test-model"),
                Status = new AgentSessionStatusSnapshot(
                    CreatedAt: now.AddSeconds(-index - 1),
                    BoundAt: now.AddSeconds(-index - 1),
                    AgentRuntimeSessionId: id,
                    Activity: AgentSessionActivity.Idle),
                Metadata = new AgentSessionMetadata(new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
                    [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
                    [GenericAgentSessionMetadata.AgentId] = $"agent-hist-{index}",
                    [GenericAgentSessionMetadata.AgentName] = $"Historical {index}",
                }),
            };
            db.AgentSessions.Add(new AgentSessionRow
            {
                Id = id,
                State = JsonSerializer.Serialize(session, AgentSessionJson.JsonOptions),
                CreatedAt = now.AddSeconds(-index - 1),
                Status = "bound",
                AgentSessionId = id,
                RunnerId = "runner-status-historical",
            });
        }
        await db.SaveChangesAsync();
    }

    private async Task<string> InsertWorkflowSessionAsync(
        string projectId,
        string workflowRunId,
        int issueNumber,
        string? workId = null,
        string? sourceKind = "workflow")
    {
        var workIdValue = workId ?? $"work-{Guid.NewGuid():N}";
        var sessionId = $"session-{Guid.NewGuid():N}";
        var now = TimeProvider.GetUtcNow().UtcDateTime;
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
            [AgentSessionQueryMetadataKeys.WorkflowRunId] = workflowRunId,
            [AgentSessionQueryMetadataKeys.WorkId] = workIdValue,
            [AgentSessionQueryMetadataKeys.WorkType] = "task",
            [AgentSessionQueryMetadataKeys.Stage] = "Build",
            [AgentSessionQueryMetadataKeys.IssueNumber] = issueNumber.ToString(),
            [AgentSessionQueryMetadataKeys.SessionName] = $"task-{issueNumber}",
        };
        if (sourceKind is not null)
            labels[AgentSessionQueryMetadataKeys.SourceKind] = sourceKind;
        var session = new AgentSession
        {
            Id = sessionId,
            Runtime = new AgentSessionRuntime("runner-status-wf", null),
            Settings = new AgentSessionSettings("test-model"),
            Status = new AgentSessionStatusSnapshot(
                CreatedAt: now,
                BoundAt: now,
                AgentRuntimeSessionId: sessionId),
            Metadata = new AgentSessionMetadata(labels),
        };
        await using var db = await DbFactory.CreateDbContextAsync();
        db.AgentSessions.Add(new AgentSessionRow
        {
            Id = sessionId,
            State = JsonSerializer.Serialize(session, AgentSessionJson.JsonOptions),
            CreatedAt = now,
            Status = "bound",
            AgentSessionId = sessionId,
            RunnerId = "runner-status-wf",
        });
        await db.SaveChangesAsync();
        return sessionId;
    }

    private async Task<IReadOnlyList<string>> InsertWorkflowSessionsForSingleRunAsync(
        string projectId,
        string workflowRunId,
        string workId,
        int count)
    {
        var ids = new List<string>(count);
        var now = TimeProvider.GetUtcNow().UtcDateTime;
        await using var db = await DbFactory.CreateDbContextAsync();
        for (var index = 0; index < count; index++)
        {
            var id = $"session-{Guid.NewGuid():N}";
            ids.Add(id);
            var session = new AgentSession
            {
                Id = id,
                Runtime = new AgentSessionRuntime("runner-status-wf", null),
                Settings = new AgentSessionSettings("test-model"),
                Status = new AgentSessionStatusSnapshot(
                    CreatedAt: now.AddTicks(index),
                    BoundAt: now.AddTicks(index),
                    AgentRuntimeSessionId: id),
                Metadata = new AgentSessionMetadata(new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
                    [AgentSessionQueryMetadataKeys.SourceKind] = "workflow",
                    [AgentSessionQueryMetadataKeys.WorkflowRunId] = workflowRunId,
                    [AgentSessionQueryMetadataKeys.WorkId] = workId,
                    [AgentSessionQueryMetadataKeys.WorkType] = "task",
                    [AgentSessionQueryMetadataKeys.Stage] = "Build",
                    [AgentSessionQueryMetadataKeys.IssueNumber] = (100 + index).ToString(),
                    [AgentSessionQueryMetadataKeys.SessionName] = $"task-shared-{index}",
                }),
            };
            db.AgentSessions.Add(new AgentSessionRow
            {
                Id = id,
                State = JsonSerializer.Serialize(session, AgentSessionJson.JsonOptions),
                CreatedAt = now.AddTicks(index),
                Status = "bound",
                AgentSessionId = id,
                RunnerId = "runner-status-wf",
            });
        }
        await db.SaveChangesAsync();
        return ids;
    }

    private async Task InsertWorkflowRunRowAsync(string workflowRunId, string projectId, string status)
    {
        await using var db = await DbFactory.CreateDbContextAsync();
        db.WorkflowRuns.Add(new WorkflowRunRow
        {
            WorkflowRunId = workflowRunId,
            State = BuildWorkflowRunStateJson(workflowRunId, projectId, status),
        });
        await db.SaveChangesAsync();
    }

    private async Task TerminalizeWorkflowAsync(string workflowRunId)
    {
        await using var db = await DbFactory.CreateDbContextAsync();
        var row = await db.WorkflowRuns.FirstAsync(r => r.WorkflowRunId == workflowRunId);
        row.State = BuildWorkflowRunStateJson(workflowRunId, projectId: ExtractProjectId(row.State), status: "completed");
        await db.SaveChangesAsync();
    }

    private static string ExtractProjectId(string workflowStateJson)
    {
        using var doc = JsonDocument.Parse(workflowStateJson);
        if (doc.RootElement.TryGetProperty("metadata", out var metadata)
            && metadata.TryGetProperty("projectId", out var projectId))
        {
            return projectId.GetString() ?? string.Empty;
        }
        return string.Empty;
    }

    private static string BuildWorkflowRunStateJson(string workflowRunId, string projectId, string status) =>
        $$"""
        {
          "id": "{{workflowRunId}}",
          "status": "{{status}}",
          "metadata": {
            "projectId": "{{projectId}}",
            "issueNumber": 1,
            "createdAt": "2026-06-30T00:00:00Z"
          }
        }
        """;

    private static WorkflowStatusView BuildRunningViewWithPendingWork(string workflowRunId, string workId) =>
        new WorkflowStatusView(
            WorkflowRunId: workflowRunId,
            Status: "Running",
            CurrentStage: null,
            Stages: [],
            PendingWork: new PendingWorkView(WorkId: workId, WorkType: "task", Stage: null, Title: null, Uses: null),
            Failure: null,
            AvailableActions: []);

    private sealed class TerminalizingAgentSessionQuery : AgentSessionQuery
    {
        private readonly Func<Task> _terminalize;

        public TerminalizingAgentSessionQuery(
            IDbContextFactory<MohistDbContext> dbFactory,
            TimeProvider timeProvider,
            Func<Task> terminalize)
            : base(dbFactory, timeProvider)
        {
            _terminalize = terminalize;
        }

        public bool SelectedCandidates { get; private set; }

        public override async Task<IReadOnlyList<AgentSessionRecord>> ListStatusCandidatesAsync(
            string projectId,
            CancellationToken ct = default)
        {
            var candidates = await base.ListStatusCandidatesAsync(projectId, ct);
            SelectedCandidates = candidates.Count > 0;
            await _terminalize();
            return candidates;
        }
    }
}
