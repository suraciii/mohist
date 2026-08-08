using Mohist.Server.Infrastructure.Data.Workspace;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Workspace.Domain;
using Mohist.Server.Workspace.Grains;

namespace Mohist.Server.Workspace.Services;

/// <summary>
/// Resolves interaction-entry (Slack channel / DM, Web conversation) origins
/// to active workspaces, creating them on first trigger and archiving them
/// when the entry disappears. Name derivation and uniqueness live here
/// because an archived workspace keeps occupying its derived name (PK),
/// so the grain-key-addressed create path used by issue/manual origins
/// cannot be reused verbatim.
/// </summary>
public sealed class InteractionWorkspaceProvisioner(
    IWorkspaceStore store,
    IGrainFactory grains) : IScopedService
{
    public Task<string> EnsureSlackWorkspaceAsync(string projectId, string teamId, string channelId, DateTimeOffset now)
        => EnsureAsync(projectId, new WorkspaceOrigin.Slack(teamId, channelId), $"slack-{channelId}", now);

    public Task<string> EnsureWebWorkspaceAsync(string projectId, string conversationId, DateTimeOffset now)
        => EnsureAsync(projectId, new WorkspaceOrigin.Web(conversationId), $"web-{conversationId}", now);

    public Task<string> EnsureCliWorkspaceAsync(string projectId, DateTimeOffset now)
        => EnsureAsync(projectId, new WorkspaceOrigin.Cli(), "cli-current", now);

    public async Task<bool> ArchiveSlackChannelAsync(string projectId, string teamId, string channelId, DateTimeOffset now)
        => await ArchiveAsync(projectId, new WorkspaceOrigin.Slack(teamId, channelId), now);

    public async Task<bool> ArchiveWebConversationAsync(string projectId, string conversationId, DateTimeOffset now)
        => await ArchiveAsync(projectId, new WorkspaceOrigin.Web(conversationId), now);

    private async Task<string> EnsureAsync(string projectId, WorkspaceOrigin origin, string baseName, DateTimeOffset now)
    {
        var active = await FindActiveAsync(projectId, origin);
        if (active is not null) return active.Name;

        var name = await DeriveUniqueNameAsync(projectId, baseName);
        try
        {
            await grains.GetGrain<IWorkspaceGrain>(GrainKey.Workspace(projectId, name))
                .CreateAsync(name, origin, [], now);
            return name;
        }
        catch (WorkspaceDomainException ex) when (ex.Code is "workspace_conflict" or "workspace_origin_conflict" or "workspace_name_taken")
        {
            var winner = await FindActiveAsync(projectId, origin);
            if (winner is not null) return winner.Name;
            throw;
        }
    }

    private async Task<string> DeriveUniqueNameAsync(string projectId, string baseName)
    {
        var candidate = baseName;
        for (var suffix = 2; await store.FindAsync(projectId, candidate) is not null; suffix++)
            candidate = $"{baseName}-{suffix}";
        return candidate;
    }

    private async Task<bool> ArchiveAsync(string projectId, WorkspaceOrigin origin, DateTimeOffset now)
    {
        var active = await FindActiveAsync(projectId, origin);
        if (active is null) return false;

        await grains.GetGrain<IWorkspaceGrain>(GrainKey.Workspace(projectId, active.Name))
            .ArchiveByOriginAsync(origin, now);
        return true;
    }

    private Task<WorkspaceState?> FindActiveAsync(string projectId, WorkspaceOrigin origin)
        => store.FindActiveByOriginAsync(
            projectId,
            WorkspaceRowJson.OriginKind(origin),
            WorkspaceRowJson.OriginPayload(origin));
}
