using System.Text;
using Mohist.Server.Slack.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Slack;

public sealed class SlackActionCodecTests
{
    private static readonly byte[] Credential = Encoding.UTF8.GetBytes("xoxb-server-test");

    [Fact]
    public void Canonicalization_includes_all_authorization_fields_and_omits_the_signature()
    {
        var payload = RetryPayload();

        var canonical = SlackActionCodec.Canonicalize(payload);

        Assert.Contains(payload.Version, canonical, StringComparison.Ordinal);
        Assert.Contains(payload.Action, canonical, StringComparison.Ordinal);
        Assert.Contains(payload.ConnectionId, canonical, StringComparison.Ordinal);
        Assert.Contains(payload.WorkspaceTeamId, canonical, StringComparison.Ordinal);
        Assert.Contains(payload.SessionId, canonical, StringComparison.Ordinal);
        Assert.Contains(payload.TurnId, canonical, StringComparison.Ordinal);
        Assert.Contains(payload.InputId, canonical, StringComparison.Ordinal);
        Assert.Contains(payload.DispatchRef, canonical, StringComparison.Ordinal);
        Assert.Contains(payload.ConversationId, canonical, StringComparison.Ordinal);
        Assert.Contains(payload.MessageTs, canonical, StringComparison.Ordinal);
        Assert.Contains(payload.ThreadTs!, canonical, StringComparison.Ordinal);
        Assert.Contains(payload.ActorSlackUserId, canonical, StringComparison.Ordinal);
        Assert.Contains(payload.Nonce, canonical, StringComparison.Ordinal);
        Assert.Contains(payload.ExpiresAt.ToUnixTimeMilliseconds().ToString(), canonical, StringComparison.Ordinal);
        Assert.DoesNotContain("signature", canonical, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(canonical, SlackActionCodec.Canonicalize(payload with { ActorSlackUserId = "U_OTHER" }));
        Assert.NotEqual(canonical, SlackActionCodec.Canonicalize(payload with { OriginalDirectMessage = true }));
    }

    [Fact]
    public void Stop_retry_and_selection_values_verify_with_the_same_constant_time_codec()
    {
        var payloads = new ISlackActionPayload[]
        {
            StopPayload(),
            RetryPayload(),
            SelectionPayload(),
        };

        foreach (var payload in payloads)
        {
            var value = SlackActionCodec.SerializeWithSignature(
                payload,
                SlackActionCodec.Sign(payload, Credential));

            Assert.DoesNotContain("xoxb-server-test", value, StringComparison.Ordinal);
            Assert.True(payload switch
            {
                SlackStopActionPayload => SlackActionCodec.TryVerify(value, Credential, out SlackStopActionPayload? _),
                SlackRetryActionPayload => SlackActionCodec.TryVerify(value, Credential, out SlackRetryActionPayload? _),
                SlackSelectionActionPayload => SlackActionCodec.TryVerify(value, Credential, out SlackSelectionActionPayload? _),
                _ => false,
            });
        }
    }

    [Fact]
    public void Tampering_with_any_action_shape_is_rejected_and_wrong_credentials_do_not_verify()
    {
        var retry = RetryPayload();
        var retryValue = SlackActionCodec.SerializeWithSignature(retry, SlackActionCodec.Sign(retry, Credential));
        var tampered = retryValue.Replace("turn-1", "turn-2", StringComparison.Ordinal);

        Assert.False(SlackActionCodec.TryVerify(tampered, Credential, out SlackRetryActionPayload? _));
        Assert.False(SlackActionCodec.TryVerify(retryValue, Encoding.UTF8.GetBytes("xoxb-other"), out SlackRetryActionPayload? _));

        var selection = SelectionPayload();
        var selectionValue = SlackActionCodec.SerializeWithSignature(selection, SlackActionCodec.Sign(selection, Credential));
        var changedCandidate = selectionValue.Replace("fingerprint-1", "fingerprint-2", StringComparison.Ordinal);

        Assert.False(SlackActionCodec.TryVerify(changedCandidate, Credential, out SlackSelectionActionPayload? _));
    }

    private static SlackStopActionPayload StopPayload() => new(
        SlackActionCodec.Version,
        SlackActionCodec.StopAction,
        "connection-1",
        "session-1",
        "turn-1",
        "input-1",
        "dispatch-1",
        "U_ACTOR",
        "U_OWNER",
        "C1",
        "100.001",
        "100.000",
        "nonce-stop",
        DateTimeOffset.Parse("2026-08-17T00:05:00Z"),
        null)
    {
        WorkspaceTeamId = "T1",
        OriginalDirectMessage = false,
    };

    private static SlackRetryActionPayload RetryPayload() => new(
        SlackActionCodec.Version,
        SlackActionCodec.RetryAction,
        "connection-1",
        "session-1",
        "turn-1",
        "input-1",
        "dispatch-1",
        "T1",
        "C1",
        "100.001",
        "100.000",
        false,
        "U_ACTOR",
        "nonce-retry",
        DateTimeOffset.Parse("2026-08-17T00:05:00Z"),
        null);

    private static SlackSelectionActionPayload SelectionPayload() => new(
        SlackActionCodec.Version,
        SlackActionCodec.SelectionAction,
        "connection-1",
        "prompt-1",
        "connection-2",
        "fingerprint-1",
        "T1",
        "C1",
        "100.001",
        null,
        false,
        "U_ACTOR",
        "nonce-selection",
        DateTimeOffset.Parse("2026-08-17T00:05:00Z"),
        null);
}
