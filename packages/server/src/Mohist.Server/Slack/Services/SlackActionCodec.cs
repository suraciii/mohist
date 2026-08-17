using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mohist.Server.Infrastructure;

namespace Mohist.Server.Slack.Services;

/// <summary>
/// Common signed-action encoding for Server-owned Slack controls. The
/// canonical form is deliberately explicit: changing any field that can
/// affect authorization changes the signature.
/// </summary>
public static class SlackActionCodec
{
    public const string Version = "v1";
    public const string StopAction = "stop";
    public const string RetryAction = "retry";
    public const string SelectionAction = "select_connection";

    public static string Canonicalize(ISlackActionPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var fields = new List<string>
        {
            payload.Version,
            payload.Action,
            payload.ConnectionId,
            payload.WorkspaceTeamId,
            payload.ConversationId,
            payload.MessageTs,
            payload.ThreadTs ?? string.Empty,
            payload.OriginalDirectMessage ? "true" : "false",
            payload.ActorSlackUserId,
        };

        switch (payload)
        {
            case SlackStopActionPayload stop:
                fields.AddRange([
                    stop.SessionId,
                    stop.TurnId,
                    stop.InputId,
                    stop.DispatchRef,
                    stop.InitiatorSlackUserId,
                ]);
                break;
            case SlackRetryActionPayload retry:
                fields.AddRange([
                    retry.SessionId,
                    retry.TurnId,
                    retry.InputId,
                    retry.DispatchRef,
                ]);
                break;
            case SlackSelectionActionPayload selection:
                fields.AddRange([
                    selection.PromptId,
                    selection.SelectedConnectionId,
                    selection.CandidateSetFingerprint,
                ]);
                break;
            default:
                throw new ArgumentException("Unsupported Slack action payload.", nameof(payload));
        }

        fields.Add(payload.Nonce);
        fields.Add(payload.ExpiresAt.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        return string.Join("\n", fields);
    }

    public static string Sign(ISlackActionPayload payload, ReadOnlySpan<byte> credential)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (credential.Length == 0)
            throw new ArgumentException("A verified Slack credential is required.", nameof(credential));

        return Convert.ToHexString(HMACSHA256.HashData(
            credential,
            Encoding.UTF8.GetBytes(Canonicalize(payload))));
    }

    public static string SerializeWithSignature(ISlackActionPayload payload, string signature)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentException.ThrowIfNullOrWhiteSpace(signature);
        return payload switch
        {
            SlackStopActionPayload stop => JSON.Serialize(stop with { Signature = signature }),
            SlackRetryActionPayload retry => JSON.Serialize(retry with { Signature = signature }),
            SlackSelectionActionPayload selection => JSON.Serialize(selection with { Signature = signature }),
            _ => throw new ArgumentException("Unsupported Slack action payload.", nameof(payload)),
        };
    }

    public static bool TryVerify<T>(
        string actionValue,
        ReadOnlySpan<byte> credential,
        out T? payload)
        where T : class, ISlackActionPayload
    {
        payload = null;
        if (string.IsNullOrWhiteSpace(actionValue) || credential.Length == 0)
            return false;

        try
        {
            payload = JsonSerializer.Deserialize<T>(actionValue, JSON.Options);
        }
        catch (JsonException)
        {
            return false;
        }

        if (payload is null
            || !string.Equals(payload.Version, Version, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(payload.Action)
            || string.IsNullOrWhiteSpace(payload.Nonce)
            || string.IsNullOrWhiteSpace(payload.Signature))
        {
            payload = null;
            return false;
        }

        byte[] actual;
        try
        {
            actual = Convert.FromHexString(payload.Signature);
        }
        catch (FormatException)
        {
            payload = null;
            return false;
        }

        var expected = HMACSHA256.HashData(
            credential,
            Encoding.UTF8.GetBytes(Canonicalize(payload)));
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            payload = null;
            return false;
        }

        return true;
    }
}

public interface ISlackActionPayload
{
    string Version { get; }
    string Action { get; }
    string ConnectionId { get; }
    string WorkspaceTeamId { get; }
    string ConversationId { get; }
    string MessageTs { get; }
    string? ThreadTs { get; }
    bool OriginalDirectMessage { get; }
    string ActorSlackUserId { get; }
    string Nonce { get; }
    DateTimeOffset ExpiresAt { get; }
    string? Signature { get; }
}

public sealed record SlackRetryActionPayload(
    string Version,
    string Action,
    string ConnectionId,
    string SessionId,
    string TurnId,
    string InputId,
    string DispatchRef,
    string WorkspaceTeamId,
    string ConversationId,
    string MessageTs,
    string? ThreadTs,
    bool OriginalDirectMessage,
    string ActorSlackUserId,
    string Nonce,
    DateTimeOffset ExpiresAt,
    string? Signature) : ISlackActionPayload;

public sealed record SlackSelectionActionPayload(
    string Version,
    string Action,
    string ConnectionId,
    string PromptId,
    string SelectedConnectionId,
    string CandidateSetFingerprint,
    string WorkspaceTeamId,
    string ConversationId,
    string MessageTs,
    string? ThreadTs,
    bool OriginalDirectMessage,
    string ActorSlackUserId,
    string Nonce,
    DateTimeOffset ExpiresAt,
    string? Signature) : ISlackActionPayload;

public sealed record SlackStopAction(
    string ActionId,
    string ActionValue,
    DateTimeOffset ExpiresAt,
    JsonElement Blocks);

public sealed record SlackStopActionPayload(
    string Version,
    string Action,
    string ConnectionId,
    string SessionId,
    string TurnId,
    string InputId,
    string DispatchRef,
    string ActorSlackUserId,
    string InitiatorSlackUserId,
    string ConversationId,
    string MessageTs,
    string? ThreadTs,
    string Nonce,
    DateTimeOffset ExpiresAt,
    string? Signature) : ISlackActionPayload
{
    // These are init-only so the v1 Stop constructor remains source-compatible
    // while newly issued values bind the receiving workspace and DM mode.
    public string WorkspaceTeamId { get; init; } = string.Empty;
    public bool OriginalDirectMessage { get; init; }
}
