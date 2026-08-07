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
    public void RedactReplyText_StripsSecretsAndNeutralizesSlackControlSyntax()
    {
        var redacted = SlackFinalReplyRenderer.RedactReplyText(
            "Result ready. token=xoxb-secret and api_key=abc123. Ping <!channel> and <@U123>.");

        Assert.Contains("Result ready.", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("xoxb-secret", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", redacted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", redacted, StringComparison.Ordinal);
        // Slack control syntax is neutralized so the Agent body cannot trigger
        // mentions/broadcasts or forge controls.
        Assert.DoesNotContain("<!channel>", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("<@U123>", redacted, StringComparison.Ordinal);
        Assert.Contains("&lt;!channel&gt;", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactReplyText_ReturnsEmptyForBlankInput()
    {
        Assert.Equal(string.Empty, SlackFinalReplyRenderer.RedactReplyText("   "));
        Assert.Equal(string.Empty, SlackFinalReplyRenderer.RedactReplyText(null));
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
    public void AppendStableReference_UsesTheSessionWhenAvailableAndNeutralizesItsMarkup()
    {
        var sessionReply = SlackFinalReplyRenderer.AppendStableReference("Completed.", "job-1", "session-1");
        var jobReply = SlackFinalReplyRenderer.AppendStableReference("Completed.", "job-1", null);
        var safeReply = SlackFinalReplyRenderer.AppendStableReference("Completed.", "job-1", "<@U123>");

        Assert.Equal("Completed.\nSession: session-1", sessionReply);
        Assert.Equal("Completed.\nJob: job-1", jobReply);
        Assert.Equal("Completed.\nSession: &lt;@U123&gt;", safeReply);
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
        var arrayText = string.Join('\n', arrayResult.Segments);
        Assert.Contains("array result: 2 items", arrayText);
        Assert.DoesNotContain("api", arrayText, StringComparison.Ordinal);
        Assert.DoesNotContain("worker", arrayText, StringComparison.Ordinal);
        Assert.Contains("scalar result: 42", string.Join('\n', scalarResult.Segments));
        Assert.Contains("embedded result: object: state=ready", string.Join('\n', embeddedResult.Segments));
        Assert.DoesNotContain("{\"state\":\"ready\"}", string.Join('\n', embeddedResult.Segments));
    }

    [Fact]
    public void Project_NeverProjectsRawMachinePayload()
    {
        var result = new SlackConfirmedAgentResult(
            "inspect the service",
            SlackFinalReplyStatus.Completed,
            MachineResults:
            [
                new SlackConfirmedMachineResult(
                    "request metadata",
                    "{\"status\":\"ok\",\"authorization\":\"Bearer do-not-leak\",\"private_url\":\"https://internal.example.test/run/123\",\"logs\":\"Authorization: Bearer log-secret\"}"),
                new SlackConfirmedMachineResult(
                    "plain output",
                    "2026-08-03T08:00:00Z Authorization: Bearer log-secret https://private.example.test/output"),
                new SlackConfirmedMachineResult(
                    "string result",
                    "\"unrecognized-secret\""),
            ]);

        var text = string.Join('\n', SlackFinalReplyRenderer.Project(result).Segments);

        Assert.Contains("request metadata: object: status=ok", text);
        Assert.Contains("plain output: machine output received; no public summary available.", text);
        Assert.DoesNotContain("Bearer", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("do-not-leak", text, StringComparison.Ordinal);
        Assert.DoesNotContain("log-secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("internal.example.test", text, StringComparison.Ordinal);
        Assert.DoesNotContain("private.example.test", text, StringComparison.Ordinal);
        Assert.DoesNotContain("unrecognized-secret", text, StringComparison.Ordinal);
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

    [Fact]
    public void SegmentReplyText_ReturnsOneSegmentForShortBody()
    {
        var segments = SlackFinalReplyRenderer.SegmentReplyText("A short final answer.");

        var single = Assert.Single(segments);
        Assert.Equal("A short final answer.", single);
    }

    [Fact]
    public void SegmentReplyText_SplitsLongBodyIntoOrderedSegmentsUnderTheLimit()
    {
        var lines = Enumerable.Range(0, 4_000)
            .Select(index => $"Line {index:D5}: the quick brown fox jumps over the lazy dog.")
            .ToList();
        var body = string.Join('\n', lines);

        var segments = SlackFinalReplyRenderer.SegmentReplyText(body, maximumSegmentLength: 1_000);

        Assert.True(segments.Count > 1);
        Assert.All(segments, segment => Assert.InRange(segment.EnumerateRunes().Count(), 1, 1_000));
        var rejoined = string.Join('\n', segments);
        Assert.Contains(lines[0], rejoined, StringComparison.Ordinal);
        Assert.Contains(lines[^1], rejoined, StringComparison.Ordinal);
        Assert.All(lines, line => Assert.Contains(line, rejoined, StringComparison.Ordinal));
    }

    [Fact]
    public void SegmentReplyText_PreservesOrderAcrossSegments()
    {
        var marker = (int index) => $"MARKER_{index:D5}";
        var body = string.Join('\n', Enumerable.Range(0, 2_000).Select(index => marker(index) + " content"));

        var segments = SlackFinalReplyRenderer.SegmentReplyText(body, maximumSegmentLength: 500);
        var rejoined = string.Join('\n', segments);

        var positions = Enumerable.Range(0, 2_000).Select(index => rejoined.IndexOf(marker(index), StringComparison.Ordinal)).ToArray();
        Assert.All(positions, position => Assert.True(position >= 0));
        Assert.Equal(positions.OrderBy(p => p), positions);
    }

    [Fact]
    public void SegmentReplyText_HandlesEmptyBodyAndRejectsNonPositiveLimit()
    {
        Assert.Empty(SlackFinalReplyRenderer.SegmentReplyText("   \n  \t  "));
        Assert.Empty(SlackFinalReplyRenderer.SegmentReplyText(null));
        Assert.Throws<ArgumentOutOfRangeException>(() => SlackFinalReplyRenderer.SegmentReplyText("body", 0));
    }
}
