using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Slack;
using Xunit;

namespace Mohist.Server.UnitTests.Slack;

public sealed class SlackThreadHistoryReaderTests
{
    [Fact]
    public async Task ReadAsync_UsesNoServerSideSlackClient_AndReturnsEmptyContext()
    {
        var result = await new SlackThreadHistoryReader().ReadAsync(
            "project", "connection", "C1", "1710.000000", "1710.000100");

        Assert.Equal(SlackThreadHistoryReadOutcome.Empty, result.Outcome);
        Assert.Empty(result.Messages);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public void ApplyBudget_UnderBudget_RendersAllMessages()
    {
        var (text, marker, omitted) = SlackThreadHistoryReader.ApplyBudget(
        [
            Message("1710.000010", "U1", "hi"),
            Message("1710.000020", "U2", "there"),
        ], 1024);

        Assert.Equal("U1: hi\nU2: there", text);
        Assert.Null(marker);
        Assert.Equal(0, omitted);
    }

    [Fact]
    public void ApplyBudget_OverBudget_DropsOldestMessages()
    {
        var longText = new string('a', 100);
        var (text, marker, omitted) = SlackThreadHistoryReader.ApplyBudget(
        [
            Message("1710.000010", "U1", longText),
            Message("1710.000020", "U2", longText),
            Message("1710.000030", "U3", longText),
        ], 250);

        Assert.Equal(1, omitted);
        Assert.Equal("1 oldest messages omitted", marker);
        Assert.DoesNotContain("U1:", text, StringComparison.Ordinal);
        Assert.Contains("U2:", text, StringComparison.Ordinal);
        Assert.Contains("U3:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyBudget_SortsMessagesBySlackTimestamp()
    {
        var (text, _, _) = SlackThreadHistoryReader.ApplyBudget(
        [
            Message("1710.000030", "U3", "third"),
            Message("1710.000010", "U1", "first"),
            Message("1710.000020", "U2", "second"),
        ], 1024);

        Assert.Equal("U1: first\nU2: second\nU3: third", text);
    }

    private static SlackConversationMessage Message(string ts, string user, string text) =>
        new("message", null, ts, user, text, null, null, null);
}
