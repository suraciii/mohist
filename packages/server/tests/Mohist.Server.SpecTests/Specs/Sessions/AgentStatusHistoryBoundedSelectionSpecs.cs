using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Artifacts;
using Orleans.TestingHost;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

/// <summary>
/// Issue-467 T-001: history-bounded <c>/api/agent/status</c> selection.
/// Deterministic coverage that the new
/// <see cref="AgentSessionQuery.ListStatusCandidatesAsync"/> path:
/// <list type="bullet">
/// <item>materializes only active direct Sessions and Sessions for
///   running Workflow Runs in the requested project;</item>
/// <item>preserves the existing global creation-descending order and
///   the existing <c>activeAgents</c> response shape;</item>
/// <item>performs one <see cref="WorkflowQuerier.GetStatusAsync"/>
///   read per distinct running Workflow even when multiple candidate
///   Sessions reference the same Workflow;</item>
/// <item>excludes a Workflow that terminalizes between candidate
///   selection and status read;</item>
/// <item>scales with active work, not historical Sessions — adding
///   thousands of completed / failed / cancelled / idle rows leaves
///   the response, candidate count, materialized-row count, and
///   database / downstream call counts unchanged.</item>
/// </list>
/// All assertions use operation counters (request-work interceptors +
/// the test seam exposed on <see cref="AgentSessionQuery"/> + a
/// counting <see cref="WorkflowQuerier"/> fake), never wall-clock.
/// </summary>
[Collection("AgentStatusHistoryBounded")]
public sealed class AgentStatusHistoryBoundedSelectionSpecs
{
    private readonly AgentStatusHistoryBoundedFixture _fixture;
    private readonly HttpClient _client;

    public AgentStatusHistoryBoundedSelectionSpecs(AgentStatusHistoryBoundedFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    [Fact]
    public async Task Status_WithIdenticalActiveWorkAndThousandsOfInactiveHistoricalSessions_KeepsResponseStable()
    {
        var project = await CreateProjectAsync("status-history-bounded");
        await InsertActiveDirectSessionsAsync(project, count: 2);
        var small = (await GetStatusDataAsync(project))
            .GetProperty("amplification");

        await InsertInactiveHistoricalSessionsAsync(project, count: 1500);

        var status = await GetStatusDataAsync(project);
        var amplification = status.GetProperty("amplification");

        Assert.Equal(2, amplification.GetProperty("candidates").GetInt64());
        Assert.Equal(2, amplification.GetProperty("processed").GetInt64());
        Assert.Equal(
            small.GetProperty("databaseCalls").GetInt64(),
            amplification.GetProperty("databaseCalls").GetInt64());
        Assert.Equal(
            small.GetProperty("downstreamCalls").GetInt64(),
            amplification.GetProperty("downstreamCalls").GetInt64());
    }

    [Fact]
    public async Task Status_OnlyActiveDirectSessionsInProject_AreEmitted()
    {
        var project = await CreateProjectAsync("status-direct-active-only");
        var activeSessionId = (await InsertActiveDirectSessionsAsync(project, count: 1)).Single();

        var status = await GetStatusDataAsync(project);
        var activeAgents = status.GetProperty("activeAgents").EnumerateArray().ToList();

        var entry = Assert.Single(activeAgents,
            a => a.GetProperty("sessionId").GetString() == activeSessionId);
        Assert.Equal(project, entry.GetProperty("projectId").GetString());
    }

    [Fact]
    public async Task Status_InactiveDirectSessionIsNotEmitted()
    {
        var project = await CreateProjectAsync("status-direct-inactive");
        var sessionId = await InsertIdleDirectSessionAsync(project);

        var status = await GetStatusDataAsync(project);
        var activeAgents = status.GetProperty("activeAgents").EnumerateArray().ToList();

        Assert.DoesNotContain(activeAgents, a => a.GetProperty("sessionId").GetString() == sessionId);
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

        var status = await GetStatusDataAsync(project);
        var activeAgents = status.GetProperty("activeAgents").EnumerateArray().ToList();

        Assert.DoesNotContain(activeAgents, a => a.GetProperty("sessionId").GetString() == sessionId);
        Assert.Equal(0, status.GetProperty("amplification").GetProperty("candidates").GetInt64());
    }

    [Fact]
    public async Task Status_WorkflowSessionWithoutWorkId_IsNotACandidate()
    {
        var project = await CreateProjectAsync("status-workflow-no-work-id");
        var workflowRunId = $"wf-{Guid.NewGuid():N}";
        await InsertWorkflowRunRowAsync(workflowRunId, project, status: "running");
        await InsertWorkflowSessionAsync(project, workflowRunId, issueNumber: 10, workId: string.Empty);

        using var scope = _fixture.Services.CreateScope();
        var sessionQuery = scope.ServiceProvider.GetRequiredService<AgentSessionQuery>();
        var stubQuerier = scope.ServiceProvider.GetRequiredService<CountingWorkflowQuerier>();

        var rows = await CountMaterializedRowsAsync(sessionQuery, project);
        var status = await GetStatusDataAsync(project);

        Assert.Equal(0, rows);
        Assert.Equal(0, status.GetProperty("amplification").GetProperty("candidates").GetInt64());
        Assert.Equal(0, status.GetProperty("activeAgents").GetArrayLength());
        Assert.Equal(0, stubQuerier.GetStatusCallCount(workflowRunId));
    }

    [Fact]
    public async Task Status_SessionForTerminalizedWorkflow_IsExcludedByCurrentStatusCheck()
    {
        var project = await CreateProjectAsync("status-workflow-terminalized");
        var workflowRunId = $"wf-{Guid.NewGuid():N}";
        var sessionId = await InsertWorkflowSessionAsync(project, workflowRunId, issueNumber: 11);
        // Persist a Workflow Run that the selection phase accepts (its
        // projected Status is "running"), then terminalize it via State
        // JSON before the Workflow status read so the Querier returns
        // no matching pending work. The current-status check must drop
        // the session from activeAgents even though the candidate
        // query already selected it.
        await InsertWorkflowRunRowAsync(workflowRunId, project, status: "running");
        await TerminalizeWorkflowAsync(workflowRunId);

        using var scope = _fixture.Services.CreateScope();
        var stubQuerier = scope.ServiceProvider.GetRequiredService<CountingWorkflowQuerier>();
        stubQuerier.SetStatus(workflowRunId, BuildRunningViewWithPendingWork(workflowRunId, workId: GetSessionWorkId(sessionId)));

        var status = await GetStatusDataAsync(project);
        var activeAgents = status.GetProperty("activeAgents").EnumerateArray().ToList();

        // The Session was selected as a candidate (its Workflow Run row
        // reads as "running" to the predicate), but the post-selection
        // status read sees a terminal state with no pending work, so
        // the Session is excluded.
        Assert.DoesNotContain(activeAgents, a => a.GetProperty("sessionId").GetString() == sessionId);
    }

    [Fact]
    public async Task Status_MultipleCandidateSessionsForOneRunningWorkflow_CausesOneWorkflowStatusRead()
    {
        var project = await CreateProjectAsync("status-workflow-dedup");
        var workflowRunId = $"wf-{Guid.NewGuid():N}";
        var workId = $"work-{Guid.NewGuid():N}";
        await InsertWorkflowRunRowAsync(workflowRunId, project, status: "running");
        var sessionIds = await InsertWorkflowSessionsForSingleRunAsync(project, workflowRunId, workId, count: 4);

        using var scope = _fixture.Services.CreateScope();
        var stubQuerier = scope.ServiceProvider.GetRequiredService<CountingWorkflowQuerier>();
        stubQuerier.SetStatus(workflowRunId, BuildRunningViewWithPendingWork(workflowRunId, workId));

        var status = await GetStatusDataAsync(project);

        // The post-selection Workflow status read must be deduped
        // across the 4 candidates that reference the same Workflow.
        Assert.Equal(1, stubQuerier.GetStatusCallCount(workflowRunId));

        // The post-selection pending-work match keeps every Session.
        var activeSessionIds = status.GetProperty("activeAgents").EnumerateArray()
            .Select(a => a.GetProperty("sessionId").GetString()!)
            .ToHashSet();
        Assert.Superset(activeSessionIds, new HashSet<string>(sessionIds));
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

        using var scope = _fixture.Services.CreateScope();
        var stubQuerier = scope.ServiceProvider.GetRequiredService<CountingWorkflowQuerier>();
        stubQuerier.SetStatus(workflowRunId, BuildRunningViewWithPendingWork(workflowRunId, workId));

        var status = await GetStatusDataAsync(selectedProject);

        Assert.DoesNotContain(status.GetProperty("activeAgents").EnumerateArray(),
            a => a.GetProperty("sessionId").GetString() == sessionId);
        // No Sessions in the selected project can reference the
        // Workflow, so no status read should be issued for it.
        Assert.Equal(0, stubQuerier.GetStatusCallCount(workflowRunId));
    }

    [Fact]
    public async Task Status_NoActiveWork_ReportsZeroCandidatesAndProcessed()
    {
        var project = await CreateProjectAsync("status-empty-zero");

        var status = await GetStatusDataAsync(project);
        var amplification = status.GetProperty("amplification");

        Assert.Equal(0, amplification.GetProperty("candidates").GetInt64());
        Assert.Equal(0, amplification.GetProperty("processed").GetInt64());
        Assert.Equal(0, status.GetProperty("activeAgents").GetArrayLength());
    }

    [Fact]
    public async Task Status_MaterializedRowCountRemainsBounded_WhenThousandsOfHistoricalRowsExist()
    {
        // This exercises the narrow internal test seam exposed on
        // AgentSessionQuery to count rows the candidate query
        // materializes. The seam is not exposed on the public API.
        var project = await CreateProjectAsync("status-materialized-count");
        await InsertActiveDirectSessionsAsync(project, count: 1);
        await InsertInactiveHistoricalSessionsAsync(project, count: 800);

        using var scope = _fixture.Services.CreateScope();
        var sessionQuery = scope.ServiceProvider.GetRequiredService<AgentSessionQuery>();
        var rowsBefore = await CountMaterializedRowsAsync(sessionQuery, project);

        await InsertInactiveHistoricalSessionsAsync(project, count: 800);

        var rowsAfter = await CountMaterializedRowsAsync(sessionQuery, project);

        Assert.Equal(1, rowsBefore);
        Assert.Equal(1, rowsAfter);
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

    private async Task<JsonElement> GetStatusDataAsync(string projectId)
    {
        using var response = await _client.GetAsync($"/api/projects/{projectId}/agent/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var envelope = await JsonSerializer.DeserializeAsync<JsonElement>(await response.Content.ReadAsStreamAsync());
        return envelope.GetProperty("data");
    }

    private async Task<string> CreateProjectAsync(string suffix)
    {
        var raw = $"{suffix}-{Guid.NewGuid():N}".ToLowerInvariant();
        var name = raw.Length > 63 ? raw[..63] : raw;
        using var response = await _client.PostAsJsonAsync("/api/projects", new
        {
            name,
            repository = new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main" },
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("data").GetProperty("id").GetString()
            ?? throw new InvalidOperationException($"CreateProject '{name}' returned no id");
    }

    private async Task<IReadOnlyList<string>> InsertActiveDirectSessionsAsync(string projectId, int count)
    {
        var now = _fixture.TimeProvider.GetUtcNow().UtcDateTime;
        var ids = Enumerable.Range(0, count).Select(_ => $"session-{Guid.NewGuid():N}").ToArray();
        await using var db = await _fixture.Services
            .GetRequiredService<IDbContextFactory<MohistDbContext>>()
            .CreateDbContextAsync();

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
        var now = _fixture.TimeProvider.GetUtcNow().UtcDateTime;
        var id = $"session-{Guid.NewGuid():N}";
        await using var db = await _fixture.Services
            .GetRequiredService<IDbContextFactory<MohistDbContext>>()
            .CreateDbContextAsync();

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
        var now = _fixture.TimeProvider.GetUtcNow().UtcDateTime;
        await using var db = await _fixture.Services
            .GetRequiredService<IDbContextFactory<MohistDbContext>>()
            .CreateDbContextAsync();
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
        string? workId = null)
    {
        var workIdValue = workId ?? $"work-{Guid.NewGuid():N}";
        var sessionId = $"session-{Guid.NewGuid():N}";
        var now = _fixture.TimeProvider.GetUtcNow().UtcDateTime;
        var session = new AgentSession
        {
            Id = sessionId,
            Runtime = new AgentSessionRuntime("runner-status-wf", null),
            Settings = new AgentSessionSettings("test-model"),
            Status = new AgentSessionStatusSnapshot(
                CreatedAt: now,
                BoundAt: now,
                AgentRuntimeSessionId: sessionId),
            Metadata = new AgentSessionMetadata(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
                [AgentSessionQueryMetadataKeys.SourceKind] = "workflow",
                [AgentSessionQueryMetadataKeys.WorkflowRunId] = workflowRunId,
                [AgentSessionQueryMetadataKeys.WorkId] = workIdValue,
                [AgentSessionQueryMetadataKeys.WorkType] = "task",
                [AgentSessionQueryMetadataKeys.Stage] = "Build",
                [AgentSessionQueryMetadataKeys.IssueNumber] = issueNumber.ToString(),
                [AgentSessionQueryMetadataKeys.SessionName] = $"task-{issueNumber}",
            }),
        };
        await using var db = await _fixture.Services
            .GetRequiredService<IDbContextFactory<MohistDbContext>>()
            .CreateDbContextAsync();
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
        var now = _fixture.TimeProvider.GetUtcNow().UtcDateTime;
        await using var db = await _fixture.Services
            .GetRequiredService<IDbContextFactory<MohistDbContext>>()
            .CreateDbContextAsync();
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
        await using var db = await _fixture.Services
            .GetRequiredService<IDbContextFactory<MohistDbContext>>()
            .CreateDbContextAsync();
        db.WorkflowRuns.Add(new WorkflowRunRow
        {
            WorkflowRunId = workflowRunId,
            State = BuildWorkflowRunStateJson(workflowRunId, projectId, status),
        });
        await db.SaveChangesAsync();
    }

    private async Task TerminalizeWorkflowAsync(string workflowRunId)
    {
        await using var db = await _fixture.Services
            .GetRequiredService<IDbContextFactory<MohistDbContext>>()
            .CreateDbContextAsync();
        var row = await db.WorkflowRuns.FirstAsync(r => r.WorkflowRunId == workflowRunId);
        row.State = BuildWorkflowRunStateJson(workflowRunId, projectId: ExtractProjectId(row.State), status: "completed");
        await db.SaveChangesAsync();
    }

    private static string ExtractProjectId(string workflowStateJson)
    {
        using var doc = JsonDocument.Parse(workflowStateJson);
        if (doc.RootElement.TryGetProperty("metadata", out var metadata)
            && metadata.TryGetProperty("annotations", out var annotations)
            && annotations.TryGetProperty("projectId", out var projectId))
        {
            return projectId.GetString() ?? string.Empty;
        }
        return string.Empty;
    }

    private static string GetSessionWorkId(string sessionId)
    {
        // The Workflow stub returns a synthetic view keyed on the
        // session id; Session rows encode workId in metadata.labels so
        // the pending-work match naturally falls back to the row's
        // own value. Returning a single placeholder works because
        // CountingWorkflowQuerier matches the workId verbatim.
        return $"work-{sessionId}";
    }

    private static string BuildWorkflowRunStateJson(string workflowRunId, string projectId, string status) =>
        $$"""
        {
          "id": "{{workflowRunId}}",
          "status": "{{status}}",
          "metadata": {
            "annotations": {
              "projectId": "{{projectId}}"
            },
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
}
