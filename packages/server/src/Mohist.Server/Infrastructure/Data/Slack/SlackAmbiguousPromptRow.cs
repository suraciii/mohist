namespace Mohist.Server.Infrastructure.Data.Slack;

/// <summary>
/// First-writer-wins advisory row that records "this ambiguous Slack
/// message has been prompted for a choose-one reply, do not prompt
/// again". Keyed by the stable Slack message identity
/// <c>(WorkspaceTeamId, ConversationId, MessageTs)</c> so concurrent
/// per-Connection ingress calls (each mentioned Bot receives the
/// event independently) collapse to one prompt via
/// <c>INSERT ... ON CONFLICT DO NOTHING</c>. The optional
/// <see cref="ThreadTs"/> is the inbound thread identity copied onto
/// the prompt delivery so a thread reply is prompted in that thread
/// and a root message is prompted at the channel root.
/// </summary>
/// <remarks>
/// The row is short-lived by design — a future cleanup job may reap
/// rows older than the Slack redelivery window. The row does not
/// participate in per-Connection cleanup
/// (<see cref="Mohist.Server.Agent.Services.IAgentConnectionProviderCleanup"/>)
/// because the prompt itself is connection-agnostic; the agent owns
/// no durable state of its own and the race-winning Connection may
/// come from any of the mentioned Bots.
/// </remarks>
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
    public DateTimeOffset PromptedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}