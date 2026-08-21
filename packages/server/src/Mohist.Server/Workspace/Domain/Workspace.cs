using Orleans;

namespace Mohist.Server.Workspace.Domain;

public enum WorkspaceStatus
{
    Active,
    Archived,
}

[GenerateSerializer]
public abstract record WorkspaceOrigin
{
    [GenerateSerializer]
    public sealed record Manual : WorkspaceOrigin;

    [GenerateSerializer]
    public sealed record Issue([property: Id(0)] int IssueNumber) : WorkspaceOrigin;

    [GenerateSerializer]
    public sealed record Slack([property: Id(0)] string TeamId, [property: Id(1)] string ChannelId) : WorkspaceOrigin;

    [GenerateSerializer]
    public sealed record Web([property: Id(0)] string ConversationId) : WorkspaceOrigin;

    [GenerateSerializer]
    public sealed record Cli : WorkspaceOrigin;
}

[GenerateSerializer]
public sealed record WorkspaceHome([property: Id(0)] string RunnerId, [property: Id(1)] string Path);

[GenerateSerializer]
public sealed class WorkspaceState
{
    [Id(0)]
    public required string ProjectId { get; init; }

    [Id(1)]
    public required string Name { get; init; }

    [Id(2)]
    public required WorkspaceOrigin Origin { get; init; }

    [Id(3)]
    public List<string> RepositoryNames { get; set; } = [];

    [Id(4)]
    public WorkspaceStatus Status { get; set; } = WorkspaceStatus.Active;

    [Id(5)]
    public WorkspaceHome? Home { get; set; }

    [Id(6)]
    public DateTimeOffset CreatedAt { get; set; }

    [Id(7)]
    public DateTimeOffset? ArchivedAt { get; set; }

    public void EnsureActive()
    {
        if (Status != WorkspaceStatus.Active)
            throw new WorkspaceDomainException("workspace_archived", $"Workspace '{Name}' is archived.");
    }

    public void AddRepository(string repositoryName) =>
        RepositoryNames.Add(repositoryName.Trim());

    public void RemoveRepository(string repositoryName) =>
        RepositoryNames.RemoveAll(r => string.Equals(
            r,
            repositoryName.Trim(),
            StringComparison.OrdinalIgnoreCase));

    public bool ArchiveByOrigin(WorkspaceOrigin origin, DateTimeOffset now)
    {
        if (Status == WorkspaceStatus.Archived)
            return false;

        if (!Equals(Origin, origin))
            throw new WorkspaceDomainException(
                "workspace_origin_mismatch",
                $"Workspace '{Name}' does not belong to origin '{OriginKind(origin)}'.");

        Archive(now);
        return true;
    }

    public void Close(DateTimeOffset now)
    {
        EnsureCloseAllowed();
        Archive(now);
    }

    public void EnsureCloseAllowed()
    {
        if (Origin is WorkspaceOrigin.Issue)
            throw new WorkspaceDomainException(
                "workspace_close_not_allowed_for_issue",
                "Issue-backed workspaces are archived by the issue lifecycle, not by manual close.",
                hint: "Finish or close the issue instead ('mo issue done <number>' / 'mo issue close <number>').");

        if (Status == WorkspaceStatus.Archived)
            throw new WorkspaceDomainException(
                "workspace_already_archived",
                $"Workspace '{Name}' is already archived.");
    }

    public WorkspaceHome EnsureMaterializedOn(string runnerId, string path)
    {
        if (Status != WorkspaceStatus.Active)
            throw new WorkspaceDomainException("workspace_archived", $"Workspace '{Name}' is archived and cannot be materialized.");

        if (Home is not null && !string.Equals(Home.RunnerId, runnerId, StringComparison.Ordinal))
            throw new WorkspaceDomainException(
                "workspace_home_claimed",
                $"Workspace '{Name}' is already materialized on runner '{Home.RunnerId}'.",
                hint: "The dispatching runner must yield its local directory; the job retries against the home runner.");

        if (Home is not null && string.Equals(Home.Path, path, StringComparison.Ordinal))
            return Home;

        Home = new WorkspaceHome(runnerId, path);
        return Home;
    }

    public bool ClearHomeIf(string runnerId)
    {
        if (Home is null || !string.Equals(Home.RunnerId, runnerId, StringComparison.Ordinal))
            return false;

        Home = null;
        return true;
    }

    public WorkspaceHome? ActiveHome() =>
        Status == WorkspaceStatus.Active ? Home : null;

    private void Archive(DateTimeOffset now)
    {
        Status = WorkspaceStatus.Archived;
        ArchivedAt = now;
    }

    private static string OriginKind(WorkspaceOrigin origin) => origin switch
    {
        WorkspaceOrigin.Issue => "issue",
        WorkspaceOrigin.Manual => "manual",
        WorkspaceOrigin.Slack => "slack",
        WorkspaceOrigin.Web => "web",
        WorkspaceOrigin.Cli => "cli",
        _ => "unknown",
    };
}
