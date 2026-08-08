using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workspace;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Workspace.Domain;

namespace Mohist.Server.Workspace.Services;

public sealed class WorkspaceQuerier : IScopedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly AgentSessionQuery _sessionQuery;
    private readonly AgentSessionQuerier _agentSessions;

    public WorkspaceQuerier(
        IDbContextFactory<MohistDbContext> dbFactory,
        AgentSessionQuery sessionQuery,
        AgentSessionQuerier agentSessions)
    {
        _dbFactory = dbFactory;
        _sessionQuery = sessionQuery;
        _agentSessions = agentSessions;
    }

    public async Task<IReadOnlyList<WorkspaceDto>> ListAsync(
        string projectId,
        string? status = null,
        string? origin = null,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var query = db.Workspaces.AsNoTracking().Where(w => w.ProjectId == projectId);
        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(w => w.Status == status.Trim().ToLowerInvariant());
        if (!string.IsNullOrWhiteSpace(origin))
            query = query.Where(w => w.OriginKind == origin.Trim().ToLowerInvariant());
        var rows = await query.OrderBy(w => w.Name).ToListAsync(ct);
        var states = rows
            .Select(WorkspaceRowJson.Deserialize)
            .Where(w => w is not null)
            .Cast<WorkspaceState>()
            .ToList();
        var boundCounts = await Task.WhenAll(
            states.Select(state => CountBoundSessionsAsync(projectId, state.Name, ct)));
        return states
            .Select((state, index) => ToDto(state) with { BoundSessionCount = boundCounts[index] })
            .ToList();
    }

    public async Task<WorkspaceDto?> GetAsync(string projectId, string name, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.Workspaces.AsNoTracking()
            .FirstOrDefaultAsync(w => w.ProjectId == projectId && w.Name == name, ct);
        var state = row is null ? null : WorkspaceRowJson.Deserialize(row);
        if (state is null)
            return null;

        var sessions = await _agentSessions.ListUnifiedSessionsByWorkspaceAsync(projectId, name, ct: ct);
        var boundCount = await CountBoundSessionsAsync(projectId, name, ct);
        return ToDto(state) with { BoundSessionCount = boundCount, Sessions = sessions };
    }

    public async Task<int> CountBoundSessionsAsync(string projectId, string workspaceName, CancellationToken ct = default)
    {
        var records = await _sessionQuery.ListByLabelsAsync(
            AgentSessionDtoMapper.Labels(
                (AgentSessionQueryMetadataKeys.ProjectId, projectId),
                (AgentSessionQueryMetadataKeys.WorkspaceName, workspaceName)),
            ct: ct);
        return records.Count;
    }

    /// <summary>
    /// Counts sessions currently bound to and actively using the
    /// workspace. A session counts when it carries the
    /// <c>mohist.io/workspace-name</c> label, is bound to a runtime
    /// session, and its recorded activity is not idle. Unknown activity
    /// (e.g. a runner the server cannot observe) blocks lifecycle
    /// mutations just like a visibly active session.
    /// </summary>
    public async Task<int> CountActiveBoundSessionsAsync(string projectId, string workspaceName, CancellationToken ct = default)
    {
        var records = await _sessionQuery.ListByLabelsAsync(
            AgentSessionDtoMapper.Labels(
                (AgentSessionQueryMetadataKeys.ProjectId, projectId),
                (AgentSessionQueryMetadataKeys.WorkspaceName, workspaceName)),
            ct: ct);
        return records.Count(record =>
            record.Session.Status.AgentRuntimeSessionId is not null
            && record.Session.Status.Activity != AgentSessionActivity.Idle);
    }

    private static WorkspaceDto ToDto(WorkspaceState state) => new(
        ProjectId: state.ProjectId,
        Name: state.Name,
        Origin: ToOriginDto(state.Origin),
        Repositories: state.RepositoryNames,
        Status: state.Status == WorkspaceStatus.Active ? "active" : "archived",
        Home: state.Home is null ? null : new WorkspaceHomeDto(state.Home.RunnerId, state.Home.Path),
        CreatedAt: state.CreatedAt.ToString("o"),
        ArchivedAt: state.ArchivedAt?.ToString("o"));

    private static WorkspaceOriginDto ToOriginDto(WorkspaceOrigin origin) => origin switch
    {
        WorkspaceOrigin.Manual => new WorkspaceOriginDto("manual"),
        WorkspaceOrigin.Issue issue => new WorkspaceOriginDto("issue", IssueNumber: issue.IssueNumber),
        WorkspaceOrigin.Slack slack => new WorkspaceOriginDto("slack", TeamId: slack.TeamId, ChannelId: slack.ChannelId),
        WorkspaceOrigin.Web web => new WorkspaceOriginDto("web", ConversationId: web.ConversationId),
        WorkspaceOrigin.Cli => new WorkspaceOriginDto("cli"),
        _ => new WorkspaceOriginDto("unknown"),
    };
}
