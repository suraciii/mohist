using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Workspace.Domain;

namespace Mohist.Server.Infrastructure.Data.Workspace;

public interface IWorkspaceStore
{
    Task<WorkspaceState?> FindAsync(string projectId, string name, CancellationToken ct = default);
    Task<WorkspaceState?> FindActiveByOriginAsync(string projectId, string originKind, string originPayloadJson, CancellationToken ct = default);
    Task<IReadOnlyList<WorkspaceState>> ListAsync(string projectId, CancellationToken ct = default);
    Task InsertAsync(WorkspaceState state, CancellationToken ct = default);
    Task SaveAsync(WorkspaceState state, CancellationToken ct = default);
}

public sealed class WorkspaceStore : IWorkspaceStore, IScopedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    public WorkspaceStore(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<WorkspaceState?> FindAsync(string projectId, string name, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.Workspaces.AsNoTracking()
            .FirstOrDefaultAsync(w => w.ProjectId == projectId && w.Name == name, ct);
        return row is null ? null : WorkspaceRowJson.Deserialize(row);
    }

    public async Task<WorkspaceState?> FindActiveByOriginAsync(string projectId, string originKind, string originPayloadJson, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.Workspaces.AsNoTracking()
            .FirstOrDefaultAsync(w => w.ProjectId == projectId
                && w.OriginKind == originKind
                && w.OriginPayloadJson == originPayloadJson
                && w.Status == "active", ct);
        return row is null ? null : WorkspaceRowJson.Deserialize(row);
    }

    public async Task<IReadOnlyList<WorkspaceState>> ListAsync(string projectId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.Workspaces.AsNoTracking()
            .Where(w => w.ProjectId == projectId)
            .OrderBy(w => w.Name)
            .ToListAsync(ct);
        return rows.Select(WorkspaceRowJson.Deserialize).Where(w => w is not null).Cast<WorkspaceState>().ToList();
    }

    public async Task InsertAsync(WorkspaceState state, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.Workspaces.Add(WorkspaceRowJson.ToRow(state));
        await db.SaveChangesAsync(ct);
    }

    public async Task SaveAsync(WorkspaceState state, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = WorkspaceRowJson.ToRow(state);
        var existing = await db.Workspaces.FindAsync([state.ProjectId, state.Name], ct);
        if (existing is null)
        {
            db.Workspaces.Add(row);
        }
        else
        {
            db.Entry(existing).CurrentValues.SetValues(row);
        }
        await db.SaveChangesAsync(ct);
    }
}

public static class WorkspaceRowJson
{
    public static WorkspaceRow ToRow(WorkspaceState state) => new()
    {
        ProjectId = state.ProjectId,
        Name = state.Name,
        OriginKind = OriginKind(state.Origin),
        OriginPayloadJson = OriginPayload(state.Origin),
        RepositoriesJson = JSON.Serialize(state.RepositoryNames),
        Status = state.Status == WorkspaceStatus.Active ? "active" : "archived",
        HomeRunnerId = state.Home?.RunnerId,
        HomePath = state.Home?.Path,
        CreatedAt = state.CreatedAt,
        ArchivedAt = state.ArchivedAt,
    };

    public static WorkspaceState? Deserialize(WorkspaceRow row)
    {
        if (string.IsNullOrWhiteSpace(row.ProjectId) || string.IsNullOrWhiteSpace(row.Name))
            return null;
        var origin = DeserializeOrigin(row.OriginKind, row.OriginPayloadJson);
        if (origin is null)
            return null;
        var repositories = JSON.Deserialize<List<string>>(row.RepositoriesJson) ?? [];
        return new WorkspaceState
        {
            ProjectId = row.ProjectId,
            Name = row.Name,
            Origin = origin,
            RepositoryNames = repositories,
            Status = row.Status == "archived" ? WorkspaceStatus.Archived : WorkspaceStatus.Active,
            Home = string.IsNullOrWhiteSpace(row.HomeRunnerId) || string.IsNullOrWhiteSpace(row.HomePath)
                ? null
                : new WorkspaceHome(row.HomeRunnerId, row.HomePath),
            CreatedAt = row.CreatedAt,
            ArchivedAt = row.ArchivedAt,
        };
    }

    public static string OriginKind(WorkspaceOrigin origin) => origin switch
    {
        WorkspaceOrigin.Manual => "manual",
        WorkspaceOrigin.Issue => "issue",
        WorkspaceOrigin.Slack => "slack",
        WorkspaceOrigin.Web => "web",
        _ => throw new ArgumentOutOfRangeException(nameof(origin)),
    };

    public static string OriginPayload(WorkspaceOrigin origin) => origin switch
    {
        WorkspaceOrigin.Manual => "{}",
        WorkspaceOrigin.Issue issue => JSON.Serialize(new { issueNumber = issue.IssueNumber }),
        WorkspaceOrigin.Slack slack => JSON.Serialize(new { teamId = slack.TeamId, channelId = slack.ChannelId }),
        WorkspaceOrigin.Web web => JSON.Serialize(new { conversationId = web.ConversationId }),
        _ => throw new ArgumentOutOfRangeException(nameof(origin)),
    };

    private static WorkspaceOrigin? DeserializeOrigin(string kind, string payloadJson)
    {
        var payload = string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson;
        return kind switch
        {
            "manual" => new WorkspaceOrigin.Manual(),
            "issue" => new WorkspaceOrigin.Issue(JSON.DeserializeOrThrow<OriginIssuePayload>(payload).IssueNumber),
            "slack" => new WorkspaceOrigin.Slack(
                JSON.DeserializeOrThrow<OriginSlackPayload>(payload).TeamId,
                JSON.DeserializeOrThrow<OriginSlackPayload>(payload).ChannelId),
            "web" => new WorkspaceOrigin.Web(JSON.DeserializeOrThrow<OriginWebPayload>(payload).ConversationId),
            _ => null,
        };
    }

    private sealed record OriginIssuePayload(int IssueNumber);

    private sealed record OriginSlackPayload(string TeamId, string ChannelId);

    private sealed record OriginWebPayload(string ConversationId);
}
