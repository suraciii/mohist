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
}
