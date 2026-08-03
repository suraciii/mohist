using Mohist.Server.Infrastructure.Slack;
using Xunit;

namespace Mohist.Server.UnitTests.Slack;

public sealed class SlackFinalReplyRendererTests
{
    [Theory]
    [InlineData(SlackFinalReplyStatus.Completed, "Completed")]
    [InlineData(SlackFinalReplyStatus.PartiallyCompleted, "Partially completed")]
    [InlineData(SlackFinalReplyStatus.Cancelled, "Cancelled")]
    [InlineData(SlackFinalReplyStatus.Blocked, "Blocked")]
    [InlineData(SlackFinalReplyStatus.Failed, "Failed")]
    public void Project_PutsTheConfirmedConclusionFirst(
        SlackFinalReplyStatus status,
        string expectedConclusion)
    {
        var projection = SlackFinalReplyRenderer.Project(new SlackConfirmedAgentResult(
            "publish the release",
            status,
            Summary: "The confirmed result is available."));

        Assert.StartsWith($"Conclusion: {expectedConclusion} - publish the release.", projection.Segments[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Project_ShowsCompletedPartsAndAtMostThreeKeyResults()
    {
        var projection = SlackFinalReplyRenderer.Project(new SlackConfirmedAgentResult(
            "prepare the release",
            SlackFinalReplyStatus.PartiallyCompleted,
            CompletedParts: ["build", "tests"],
            KeyResults: ["artifact uploaded", "deployment waiting", "fourth fact"]));
        var text = string.Join('\n', projection.Segments);

        Assert.Contains("- Completed: build", text);
        Assert.Contains("- Completed: tests", text);
        Assert.Contains("- artifact uploaded", text);
        Assert.DoesNotContain("deployment waiting", text);
        Assert.DoesNotContain("fourth fact", text);
        Assert.Equal(3, text.Split('\n').Count(line => line.StartsWith("- ", StringComparison.Ordinal)));
    }

    [Fact]
    public void Project_RendersBlockingFailureActionsAndExplicitNextStep()
    {
        var blocked = SlackFinalReplyRenderer.Project(new SlackConfirmedAgentResult(
            "deploy the release",
            SlackFinalReplyStatus.Blocked,
            BlockingReason: "approval is still required",
            Actions: ["Approve the deployment", "Ask me to continue"],
            NextStep: "Approve it, then continue the deployment."));
        var failed = SlackFinalReplyRenderer.Project(new SlackConfirmedAgentResult(
            "run the migration",
            SlackFinalReplyStatus.Failed,
            FailureReason: "the database was unreachable"));

        var blockedText = string.Join('\n', blocked.Segments);
        var failedText = string.Join('\n', failed.Segments);
        Assert.Contains("Blocked because: approval is still required", blockedText);
        Assert.Contains("Actions:\n- Approve the deployment\n- Ask me to continue", blockedText);
        Assert.Contains("Next step: Approve it, then continue the deployment.", blockedText);
        Assert.Contains("Failure reason: the database was unreachable", failedText);
    }

    [Fact]
    public void Project_DoesNotExposeSecretsJsonOrInternalResultStreams()
    {
        var projection = SlackFinalReplyRenderer.Project(new SlackConfirmedAgentResult(
            "inspect the service",
            SlackFinalReplyStatus.Completed,
            Summary: "token=xoxb-sensitive",
            KeyResults:
            [
                "{\"tool\":\"shell\",\"reasoning\":\"hidden\"}",
                "service is healthy",
            ]));
        var text = string.Join('\n', projection.Segments);

        Assert.DoesNotContain("xoxb-sensitive", text);
        Assert.DoesNotContain("tool", text);
        Assert.DoesNotContain("reasoning", text);
        Assert.Contains("service is healthy", text);
    }

    [Fact]
    public void Project_SegmentsLongResultsWithoutChangingOrder()
    {
        var result = new SlackConfirmedAgentResult(
            "write the release notes",
            SlackFinalReplyStatus.Completed,
            Summary: "first summary sentence with enough detail to require multiple deterministic segments",
            Actions: ["review the notes and send the next request"]);

        var first = SlackFinalReplyRenderer.Project(result, maximumSegmentLength: 48);
        var second = SlackFinalReplyRenderer.Project(result, maximumSegmentLength: 48);

        Assert.True(first.Segments.Count > 1);
        Assert.Equal(first.Segments, second.Segments);
        Assert.All(first.Segments, segment => Assert.InRange(segment.Length, 1, 48));
        var text = string.Join('\n', first.Segments);
        Assert.Contains("first summary sentence", text);
        Assert.Contains("review the notes and send the next request", text);
    }

    [Fact]
    public void Project_RequiresWorkLabelAndPositiveSegmentLimit()
    {
        Assert.Throws<ArgumentException>(() => SlackFinalReplyRenderer.Project(
            new SlackConfirmedAgentResult(" ", SlackFinalReplyStatus.Completed)));
        Assert.Throws<ArgumentOutOfRangeException>(() => SlackFinalReplyRenderer.Project(
            new SlackConfirmedAgentResult("work", SlackFinalReplyStatus.Completed), 0));
    }
}
