using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.Tests.Sessions;

/// <summary>
/// Issue-421: the Workspace-cleanup guard must judge canonical Session and Turn
/// state, never observation freshness. A Session whose evidence aged out is still
/// unverified work, so it must keep blocking a Workspace reclaim; only a terminal
/// Turn or an idle Session releases it.
/// </summary>
[Trait("level", "L0")]
public sealed class UnsettledWorkspaceOwnerSpecs : WorkflowActivityHistoryTestSupport
{
    [Fact]
    public async Task ExecutingTurnWithoutFreshEvidence_StillOwnsItsIssue()
    {
        var project = await CreateProjectAsync("cleanup-aged-evidence");
        await InsertSessionAsync(project, issueNumber: 421, AgentSessionActivity.Active, AgentTurnStatus.Executing, lastDataAt: null);

        var unsettled = await CreateQuerier().ListUnsettledIssueNumbersAsync(project);

        Assert.Contains(421, unsettled);
    }

    [Fact]
    public async Task QueuedTurn_StillOwnsItsIssue()
    {
        var project = await CreateProjectAsync("cleanup-queued");
        await InsertSessionAsync(project, issueNumber: 422, AgentSessionActivity.Active, AgentTurnStatus.Queued, lastDataAt: null);

        var unsettled = await CreateQuerier().ListUnsettledIssueNumbersAsync(project);

        Assert.Contains(422, unsettled);
    }

    [Fact]
    public async Task ActiveSessionWithoutTurnRecords_StillOwnsItsIssue()
    {
        var project = await CreateProjectAsync("cleanup-missing-evidence");
        await InsertSessionAsync(project, issueNumber: 423, AgentSessionActivity.Active, turnStatus: null, lastDataAt: null);

        var unsettled = await CreateQuerier().ListUnsettledIssueNumbersAsync(project);

        Assert.Contains(423, unsettled);
    }

    [Fact]
    public async Task TerminalTurnAndIdleSession_ReleaseTheIssue()
    {
        var project = await CreateProjectAsync("cleanup-terminal");
        await InsertSessionAsync(project, issueNumber: 424, AgentSessionActivity.Idle, AgentTurnStatus.Completed, lastDataAt: null);

        var unsettled = await CreateQuerier().ListUnsettledIssueNumbersAsync(project);

        Assert.DoesNotContain(424, unsettled);
    }

    [Fact]
    public async Task SessionInAnotherProject_DoesNotOwnTheRequestedProjectIssue()
    {
        var project = await CreateProjectAsync("cleanup-scope");
        var other = await CreateProjectAsync("cleanup-scope-other");
        await InsertSessionAsync(other, issueNumber: 425, AgentSessionActivity.Active, AgentTurnStatus.Executing, lastDataAt: null);

        var unsettled = await CreateQuerier().ListUnsettledIssueNumbersAsync(project);

        Assert.DoesNotContain(425, unsettled);
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

    private async Task InsertSessionAsync(
        string projectId,
        int issueNumber,
        AgentSessionActivity activity,
        AgentTurnStatus? turnStatus,
        DateTime? lastDataAt)
    {
        var now = TimeProvider.GetUtcNow().UtcDateTime;
        var id = $"session-{Guid.NewGuid():N}";
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
            [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
            [AgentSessionQueryMetadataKeys.IssueNumber] = issueNumber.ToString(),
            [GenericAgentSessionMetadata.AgentId] = "agent-cleanup",
            [GenericAgentSessionMetadata.AgentName] = "Cleanup Agent",
        };
        var session = new AgentSession
        {
            Id = id,
            Runtime = new AgentSessionRuntime("runner-cleanup", null),
            Settings = new AgentSessionSettings("test-model"),
            Status = new AgentSessionStatusSnapshot(
                CreatedAt: now,
                BoundAt: now,
                LastDataAt: lastDataAt,
                AgentRuntimeSessionId: id,
                Activity: activity,
                Turns: turnStatus is null
                    ? null
                    : [new AgentTurnRecord($"turn-{id}", 1, [$"input-{id}"], turnStatus.Value, UpdatedAt: lastDataAt)]),
            Metadata = new AgentSessionMetadata(labels),
        };
        await using var db = await DbFactory.CreateDbContextAsync();
        db.AgentSessions.Add(new AgentSessionRow
        {
            Id = id,
            State = JsonSerializer.Serialize(session, AgentSessionJson.JsonOptions),
            CreatedAt = now,
            Status = "bound",
            AgentSessionId = id,
            RunnerId = "runner-cleanup",
        });
        await db.SaveChangesAsync();
    }
}
