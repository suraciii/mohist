using Mohist.Server.Infrastructure.Slack;

namespace Mohist.Server.Api;

public sealed class SlackIngressBody
{
    public string EventType { get; init; } = "message";
    public bool IsDirectMessage { get; init; } = true;
    public string TeamId { get; init; } = string.Empty;
    public string ConversationId { get; init; } = string.Empty;
    public string MessageTs { get; init; } = string.Empty;
    public string? ThreadTs { get; init; }
    public IReadOnlyList<string> MentionedUserIds { get; init; } = Array.Empty<string>();
    public string? SenderSlackUserId { get; init; }
    public string? SenderKind { get; init; }
    public SlackBotAuthorMetadata? AuthorBot { get; init; }
    public string? Text { get; init; }
    public IReadOnlyList<SlackIngressFile> Files { get; init; } = Array.Empty<SlackIngressFile>();
    public string LeaseId { get; init; } = string.Empty;
    public string AdapterId { get; init; } = string.Empty;
}

public sealed class DeliveryClaimBody
{
    public string LeaseId { get; init; } = string.Empty;
    public string AdapterId { get; init; } = string.Empty;
}

public sealed class DeliveryAckBody
{
    public string Id { get; init; } = string.Empty;
    public string LeaseId { get; init; } = string.Empty;
    public string AdapterId { get; init; } = string.Empty;
    public string Outcome { get; init; } = string.Empty;
    public string? Reason { get; init; }
    public SlackProviderMessageIdentity? ProviderMessageIdentity { get; init; }
}
