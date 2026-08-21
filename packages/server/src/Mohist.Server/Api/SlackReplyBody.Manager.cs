namespace Mohist.Server.Api;

public sealed partial class SlackReplyBody
{
    // Manager replies carry the full immutable origin when the Agent supplies it.
    public string? WorkspaceTeamId { get; init; }
    public string? ProjectId { get; init; }
    public string? OwnerKind { get; init; }
    public string? ConnectionId { get; init; }
    public string? ThreadRootMessageId { get; init; }
    public string? TriggeringMessageId { get; init; }
    public string? ActorId { get; init; }
    public string? EnrollmentId { get; init; }
    public string? SessionId { get; init; }
    public string? DispatchRef { get; init; }
}
