using System.Text;
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
            Summary: "The user requested the literal JSON text {\"status\":\"ok\"}.",
            KeyResults:
            [
                "service is healthy",
            ],
            MachineResults:
            [
                new SlackConfirmedMachineResult(
                    "shell result",
                    "{\"tool\":\"shell\",\"reasoning\":\"hidden\",\"status\":\"ok\",\"token\":\"xoxb-sensitive\"}"),
            ]));
        var text = string.Join('\n', projection.Segments);

        Assert.DoesNotContain("xoxb-sensitive", text);
        Assert.DoesNotContain("tool", text);
        Assert.DoesNotContain("reasoning", text);
        Assert.Contains("literal JSON text {\"status\":\"ok\"}", text);
        Assert.Contains("service is healthy", text);
    }

    [Fact]
    public void Project_NeutralizesSlackMarkupAsVisibleText()
    {
        var projection = SlackFinalReplyRenderer.Project(new SlackConfirmedAgentResult(
            "review the release",
            SlackFinalReplyStatus.Completed,
            Summary: "Notify <!channel>, <!here>, <@U123>, <@U123|Alice>, <#C123|alerts> and <https://example.test|release notes>."));
        var text = string.Join('\n', projection.Segments);

        Assert.Contains("&lt;!channel&gt;", text);
        Assert.Contains("&lt;!here&gt;", text);
        Assert.Contains("&lt;@U123&gt;", text);
        Assert.Contains("&lt;@U123|Alice&gt;", text);
        Assert.Contains("&lt;#C123|alerts&gt;", text);
        Assert.Contains("&lt;https://example.test|release notes&gt;", text);
        Assert.DoesNotContain("<!channel>", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<@U123>", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_PreservesHumanTextAndSummarizesMachineResults()
    {
        var humanText = SlackFinalReplyRenderer.Project(new SlackConfirmedAgentResult(
            "inspect the result",
            SlackFinalReplyStatus.Completed,
            Summary: "The confirmed answer is true.",
            KeyResults: ["status: false and service is healthy"]));
        var objectResult = SlackFinalReplyRenderer.Project(new SlackConfirmedAgentResult(
            "inspect the object",
            SlackFinalReplyStatus.Completed,
            MachineResults: [new SlackConfirmedMachineResult("object result", "{\"status\":\"ready\",\"count\":2}")]));
        var arrayResult = SlackFinalReplyRenderer.Project(new SlackConfirmedAgentResult(
            "inspect the array",
            SlackFinalReplyStatus.Completed,
            MachineResults: [new SlackConfirmedMachineResult("array result", "[\"api\",\"worker\"]")]));
        var scalarResult = SlackFinalReplyRenderer.Project(new SlackConfirmedAgentResult(
            "inspect the scalar",
            SlackFinalReplyStatus.Completed,
            MachineResults: [new SlackConfirmedMachineResult("scalar result", "42")]));
        var embeddedResult = SlackFinalReplyRenderer.Project(new SlackConfirmedAgentResult(
            "inspect the embedded result",
            SlackFinalReplyStatus.Completed,
            MachineResults: [new SlackConfirmedMachineResult("embedded result", "received {\"state\":\"ready\"} from the tool")]));

        Assert.Contains("The confirmed answer is true.", string.Join('\n', humanText.Segments));
        Assert.Contains("status: false and service is healthy", string.Join('\n', humanText.Segments));
        Assert.Contains("object result: object: status=ready; count=2", string.Join('\n', objectResult.Segments));
        Assert.Contains("array result: 2 items: api; worker", string.Join('\n', arrayResult.Segments));
        Assert.Contains("scalar result: 42", string.Join('\n', scalarResult.Segments));
        Assert.Contains("embedded result: object: state=ready", string.Join('\n', embeddedResult.Segments));
        Assert.DoesNotContain("{\"state\":\"ready\"}", string.Join('\n', embeddedResult.Segments));
    }

    [Fact]
    public void Project_AllowsDiagnosticExpansionOnlyWhenRequested()
    {
        var result = new SlackConfirmedAgentResult(
            "inspect the service",
            SlackFinalReplyStatus.Completed,
            MachineResults: [new SlackConfirmedMachineResult("payload", "{\"status\":\"ok\"}")]);

        var summary = string.Join('\n', SlackFinalReplyRenderer.Project(result).Segments);
        var diagnostic = string.Join('\n', SlackFinalReplyRenderer.Project(
            result,
            detailLevel: SlackFinalReplyDetailLevel.Diagnostic).Segments);

        Assert.DoesNotContain("{\"status\":\"ok\"}", summary);
        Assert.Contains("{\"status\":\"ok\"}", diagnostic);
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
        Assert.All(first.Segments, segment => Assert.InRange(segment.EnumerateRunes().Count(), 1, 48));
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

    [Fact]
    public void Project_SegmentsUnicodeWithoutSplittingSurrogatePairs()
    {
        var summary = string.Concat(Enumerable.Repeat("🚀", 40));
        var result = new SlackConfirmedAgentResult(
            "publish the emoji report",
            SlackFinalReplyStatus.Completed,
            Summary: summary);

        var first = SlackFinalReplyRenderer.Project(result, maximumSegmentLength: 25);
        var second = SlackFinalReplyRenderer.Project(result, maximumSegmentLength: 25);

        Assert.Equal(first.Segments, second.Segments);
        Assert.All(first.Segments, segment =>
        {
            Assert.InRange(segment.EnumerateRunes().Count(), 1, 25);
        });
        var text = string.Join('\n', first.Segments);
        Assert.DoesNotContain("\uFFFD", text);
        Assert.Equal(40, text.EnumerateRunes().Count(rune => rune.Value == 0x1F680));
    }
}
