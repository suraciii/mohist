namespace Mohist.Server.Infrastructure.Slack;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Snapshot the producer hands to <see cref="SlackOutboxStore.EnqueueAsync"/>.
/// <see cref="DispatchRef"/> ties multiple progress updates for the same
/// logical dispatch together; when a ReplaceableProgress row arrives for
/// an existing <c>(ConnectionId, DispatchRef)</c> Pending entry, the
/// store updates the existing row's payload rather than inserting a new
/// one. Non-replaceable kinds MUST NOT collide on an existing Pending
/// entry — terminal results stand alone so they are never silently
/// merged into a superseded progress update.
/// </summary>
public sealed record SlackOutboxDraft(
    string ProjectId,
    string ConnectionId,
    string WorkspaceTeamId,
    string ConversationId,
    string Kind,
    string? DispatchRef,
    string PayloadJson,
    string? ThreadTs = null,
    string OwnerKind = SlackDeliveryOwnerKinds.Connection);

/// <summary>
/// Result of <see cref="SlackOutboxStore.EnqueueAsync"/>. When
/// <see cref="MergedIntoExisting"/> is true the draft's payload
/// replaced the existing ReplaceableProgress row and no new row was
/// created — the caller may continue as if a fresh insert succeeded.
/// </summary>
public sealed record SlackOutboxEnqueueResult(string Id, bool MergedIntoExisting, bool Suppressed = false);

public sealed record SlackDeliveryPayload(
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("text")] string? Text = null,
    [property: JsonPropertyName("clientMessageId")] string? ClientMessageId = null,
    [property: JsonPropertyName("providerMessageIdentity")] SlackProviderMessageIdentity? ProviderMessageIdentity = null,
    [property: JsonPropertyName("targetMessageIdentity")] SlackProviderMessageIdentity? TargetMessageIdentity = null,
    [property: JsonPropertyName("reaction")] string? Reaction = null,
    [property: JsonPropertyName("fallbackText")] string? FallbackText = null,
    [property: JsonPropertyName("fallbackDispatchRef")] string? FallbackDispatchRef = null,
    [property: JsonPropertyName("statusDispatchRef")] string? StatusDispatchRef = null,
    [property: JsonPropertyName("blocks")] JsonElement? Blocks = null,
    [property: JsonPropertyName("fileName")] string? FileName = null,
    [property: JsonPropertyName("fileContentBase64")] string? FileContentBase64 = null)
{
    public static SlackDeliveryPayload Parse(string payloadJson)
    {
        var payload = JsonSerializer.Deserialize<SlackDeliveryPayload>(payloadJson);
        return payload is null
            ? throw new InvalidOperationException("Slack delivery payload is invalid.")
            : payload with { Operation = string.IsNullOrWhiteSpace(payload.Operation) ? SlackDeliveryOperations.PostMessage : payload.Operation };
    }
}

/// <summary>
/// Read model for the outbox. Mirrors the row columns plus the
/// per-state booleans that surface the lifecycle to operator tooling.
/// </summary>
public sealed class SlackOutboxEntry
{
    public string Id { get; init; } = string.Empty;
    public string ProjectId { get; init; } = string.Empty;
    public string ConnectionId { get; init; } = string.Empty;
    public string OwnerKind { get; init; } = SlackDeliveryOwnerKinds.Connection;
    public string WorkspaceTeamId { get; init; } = string.Empty;
    public string ConversationId { get; init; } = string.Empty;
    public string? ThreadTs { get; init; }
    public string Kind { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public string? DispatchRef { get; init; }
    public string PayloadJson { get; init; } = string.Empty;
    public int AttemptCount { get; init; }
    public DateTimeOffset? NextAttemptAt { get; init; }
    public DateTimeOffset? ClaimedAt { get; init; }
    public string? ClaimedByAdapterId { get; init; }
    public DateTimeOffset? DeliveredAt { get; init; }
    public DateTimeOffset? DeliveryUncertainAt { get; init; }
    public DateTimeOffset? DeadLetteredAt { get; init; }
    public string? LastError { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record SlackOutboxList(IReadOnlyList<SlackOutboxEntry> Entries);

/// <summary>
/// Outcome of an Agent-authored reply action
/// (<c>mo slack message send</c>). The reply lands in the same outbox as
/// liveness projections; when a replaceable progress message exists it is
/// promoted in place (one input = one final answer), repeated sends merge
/// their text, and the stable dispatch reference protects against
/// duplication. <see cref="Accepted"/> is false only when no live
/// Connection owns the conversation.
/// </summary>
public sealed record SlackAgentReplyResult(
    bool Accepted,
    string? ConnectionId = null,
    string? DeliveryId = null,
    string? DispatchRef = null,
    bool MergedIntoExisting = false);

public static class SlackDeliveryOwnerKinds
{
    public const string Connection = "connection";
    public const string Manager = "manager";

    public static bool IsDefined(string? value) => value is Connection or Manager;
}

public static class SlackDeliveryOwnerIds
{
    public const string ManagerProjectId = "__mohist_slack_manager__";
}
