using Mohist.Server.Infrastructure.Slack;

namespace Mohist.Server.Infrastructure.Data.Slack;

/// <summary>
/// First-writer-wins ambiguity claim and the durable authority for a later
/// Slack agent selection. Facts and candidate references are captured in the
/// same insert as the once-only fence; the selection path must never rebuild
/// them from the prompt owner's Project or the original Slack text.
/// </summary>
public sealed class SlackAmbiguousPromptRow
{
    public string Id { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string WorkspaceTeamId { get; set; } = string.Empty;
    public string ConversationId { get; set; } = string.Empty;
    public string MessageTs { get; set; } = string.Empty;
    public string? ThreadTs { get; set; }
    public string WinningConnectionId { get; set; } = string.Empty;
    public string MentionedConnectionIdsJson { get; set; } = "[]";

    public string SenderSlackUserId { get; set; } = string.Empty;
    public string TaskText { get; set; } = string.Empty;
    public string FilesJson { get; set; } = "[]";
    public string AmbiguityKind { get; set; } = string.Empty;
    public string CandidateReferencesJson { get; set; } = "[]";

    public string SelectionState { get; set; } = SlackSelectionStates.Pending;
    public string? ChosenProjectId { get; set; }
    public string? ChosenConnectionId { get; set; }
    public string? DispatchKind { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? SelectionSessionId { get; set; }
    public string? SelectionInputId { get; set; }
    public string? SelectionTurnId { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? SettleReason { get; set; }

    public DateTimeOffset PromptedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
