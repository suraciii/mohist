using Mohist.Server.Sessions;

namespace Mohist.Server.Workspace.Services;

public sealed record WorkspaceOriginDto(
    string Kind,
    int? IssueNumber = null,
    string? TeamId = null,
    string? ChannelId = null,
    string? ConversationId = null);

public sealed record WorkspaceHomeDto(string RunnerId, string Path);

public sealed record WorkspaceDto(
    string ProjectId,
    string Name,
    WorkspaceOriginDto Origin,
    IReadOnlyList<string> Repositories,
    string Status,
    WorkspaceHomeDto? Home,
    string CreatedAt,
    string? ArchivedAt,
    int BoundSessionCount = 0,
    IReadOnlyList<UnifiedSessionListItemDto>? Sessions = null);
