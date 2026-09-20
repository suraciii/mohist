using Mohist.Server.Slack.Services;
using Xunit;

namespace Mohist.Server.Tests.Slack;

[Trait("level", "L0")]
public sealed class SlackThreadContinuationTests
{
    private const string BotToken = "xoxb-secret";
    private static readonly SlackThreadContinuationScope Scope = new(
        "project-1", "session-1", "connection-1", "T123", "C-channel", "1710.0001");

    [Fact]
    public void Encode_then_decode_round_trips_the_cursor()
    {
        var token = SlackThreadContinuation.Encode(BotToken, Scope, "provider-cursor-1");

        Assert.True(SlackThreadContinuation.TryDecode(BotToken, token, Scope, out var cursor));
        Assert.Equal("provider-cursor-1", cursor);
    }

    [Fact]
    public void Encoded_token_does_not_expose_the_provider_cursor()
    {
        var token = SlackThreadContinuation.Encode(BotToken, Scope, "provider-cursor-1");

        Assert.DoesNotContain("provider-cursor-1", token, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("project-2", "session-1", "connection-1", "T123", "C-channel", "1710.0001")]
    [InlineData("project-1", "session-2", "connection-1", "T123", "C-channel", "1710.0001")]
    [InlineData("project-1", "session-1", "connection-2", "T123", "C-channel", "1710.0001")]
    [InlineData("project-1", "session-1", "connection-1", "T999", "C-channel", "1710.0001")]
    [InlineData("project-1", "session-1", "connection-1", "T123", "C-other", "1710.0001")]
    [InlineData("project-1", "session-1", "connection-1", "T123", "C-channel", "1710.0009")]
    public void Decode_refuses_a_token_issued_for_another_scope(
        string projectId, string sessionId, string connectionId, string teamId, string channelId, string rootTs)
    {
        var token = SlackThreadContinuation.Encode(BotToken, Scope, "provider-cursor-1");

        Assert.False(SlackThreadContinuation.TryDecode(
            BotToken,
            token,
            new SlackThreadContinuationScope(projectId, sessionId, connectionId, teamId, channelId, rootTs),
            out var cursor));
        Assert.Null(cursor);
    }

    [Fact]
    public void Decode_refuses_a_token_signed_with_another_connection_credential()
    {
        var token = SlackThreadContinuation.Encode("xoxb-other", Scope, "provider-cursor-1");

        Assert.False(SlackThreadContinuation.TryDecode(BotToken, token, Scope, out _));
    }

    [Fact]
    public void Decode_refuses_a_tampered_cursor()
    {
        var token = SlackThreadContinuation.Encode(BotToken, Scope, "provider-cursor-1");
        var separator = token.LastIndexOf('.');
        var tampered = SlackThreadContinuation.Encode(BotToken, Scope, "provider-cursor-2")[..separator]
            + token[separator..];

        Assert.False(SlackThreadContinuation.TryDecode(BotToken, tampered, Scope, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-token")]
    [InlineData(".")]
    [InlineData("Y3Vyc29y.")]
    [InlineData(".deadbeef")]
    [InlineData("Y3Vyc29y.not-a-signature")]
    [InlineData("Y3Vyc29y=deadbeef")]
    public void Decode_refuses_a_malformed_token(string? token)
    {
        Assert.False(SlackThreadContinuation.TryDecode(BotToken, token, Scope, out _));
    }

    [Fact]
    public void Encode_requires_a_cursor_and_a_bot_credential()
    {
        Assert.Throws<ArgumentException>(() => SlackThreadContinuation.Encode(BotToken, Scope, " "));
        Assert.Throws<ArgumentException>(() => SlackThreadContinuation.Encode(" ", Scope, "cursor"));
    }
}
