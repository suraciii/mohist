using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Sessions;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;
using Xunit;

namespace Mohist.Server.Tests.Sessions;

[Trait("level", "L0")]
public sealed class SessionTranscriptPublicTextTests
{
    private static readonly DateTime At = new(2026, 9, 8, 0, 0, 0, DateTimeKind.Utc);

    public static TheoryData<string, string> KnownSections => new()
    {
        { "[mohist-agent-session-startup]", "[/mohist-agent-session-startup]" },
        { "[mohist-workspace-anchor]", "[/mohist-workspace-anchor]" },
        { "[mohist-execution-definition]", "[/mohist-execution-definition]" },
        { "[mohist-system-facts]", "[/mohist-system-facts]" },
        { "<openviking-context>", "</openviking-context>" },
        { "<openviking-context source=\"auto-recall\" format=\"digest\">", "</openviking-context>" },
        { "<openviking-context source=\"session-resume\" format=\"archive-digest\">", "</openviking-context>" },
        { "<openviking-context source=\"session-start\">", "</openviking-context>" },
    };

    [Theory]
    [MemberData(nameof(KnownSections))]
    public void Build_PublicProjectsEveryTextRoleAndPreservesAllOutsideParagraphs(string opening, string closing)
    {
        const string before = "First paragraph.\n\nSecond paragraph.\n\n";
        const string after = "\n\nThird paragraph.\n\nFourth paragraph.";
        var original = before + opening + "internal context" + closing + after;
        var data = Transcript(original);

        AssertText(SessionTranscriptBuilder.Build(data), before + after);
        AssertText(SessionTranscriptBuilder.Build(data, CanonicalSession(original)), before + after);
        AssertText(SessionTranscriptBuilder.Build(data, view: "raw"), original);
        AssertText(SessionTranscriptBuilder.Build(data, CanonicalSession(original), "raw"), original);
        Assert.Equal(original, data.Turns[0].PromptText);
        Assert.All(data.Parts, part => Assert.Equal(original, part.Text));
    }

    [Theory]
    [MemberData(nameof(KnownSections))]
    public void Build_EmptyPublicTextKeepsTurnStateWithoutFabricatingMessages(string opening, string closing)
    {
        var original = opening + "internal context" + closing;
        var data = Transcript(original);
        var session = CanonicalSession(original);

        var projected = Assert.Single(SessionTranscriptBuilder.Build(data, session).Turns);
        Assert.Empty(projected.User.Text);
        Assert.Empty(projected.Assistant);
        Assert.Equal("queued", projected.Status);
        Assert.True(projected.Incomplete);
        var fallback = Assert.Single(SessionTranscriptBuilder.Build(data).Turns);
        Assert.Empty(fallback.User.Text);
        Assert.Empty(fallback.Assistant);
        AssertText(SessionTranscriptBuilder.Build(data, session, "raw"), original);
    }

    [Theory]
    [MemberData(nameof(KnownSections))]
    public void Build_UnclosedOrUnopenedKnownSectionsStayVisible(string opening, string closing)
    {
        foreach (var original in new[] { " before\n\n" + opening + "unfinished", "unopened" + closing + "\n after " })
        {
            AssertText(SessionTranscriptBuilder.Build(Transcript(original)), original);
            AssertText(SessionTranscriptBuilder.Build(Transcript(original), CanonicalSession(original)), original);
        }
    }

    [Theory]
    [InlineData("  Ordinary text\n\nwith multiple paragraphs.  ")]
    [InlineData("[mohist-system-facts-extra]keep me[/mohist-system-facts-extra]")]
    [InlineData("[MOHIST-SYSTEM-FACTS]keep me[/MOHIST-SYSTEM-FACTS]")]
    [InlineData("[mohist-system-facts]keep me[/mohist-system-fact]")]
    [InlineData("<openviking-contextual>keep me</openviking-contextual>")]
    [InlineData("<openviking-context source=\"unknown\">keep me</openviking-context>")]
    [InlineData("<openviking-context source=\"auto-recall\">keep me</openviking-context>")]
    [InlineData("Parent issue context (read-only background; JSON):\n{}\n\nFirst paragraph.\n\nLast paragraph.")]
    public void Build_OrdinaryAndUnknownTextIsUnchanged(string original)
    {
        AssertText(SessionTranscriptBuilder.Build(Transcript(original)), original);
        AssertText(SessionTranscriptBuilder.Build(Transcript(original), CanonicalSession(original)), original);
    }

    [Fact]
    public void Build_MultipleCompleteSectionsPreserveInterleavedTextAndUnknownBlocks()
    {
        const string original = "first[mohist-system-facts]a[/mohist-system-facts]" +
            "\n\n[unknown]keep[/unknown]\n\n" +
            "<openviking-context source=\"session-start\">b</openviking-context>" +
            "middle[mohist-system-facts]c[/mohist-system-facts]last";
        const string expected = "first\n\n[unknown]keep[/unknown]\n\nmiddlelast";

        AssertText(SessionTranscriptBuilder.Build(Transcript(original)), expected);
        AssertText(SessionTranscriptBuilder.Build(Transcript(original), view: "raw"), original);
    }

    [Theory]
    [InlineData(false, "")]
    [InlineData(true, "Attachment input")]
    public void Build_OnlyActualAttachmentEvidenceProducesAttachmentInput(bool hasAttachment, string expected)
    {
        const string original = "[mohist-system-facts]context[/mohist-system-facts]";
        var session = CanonicalSession(original, hasAttachment);

        var turn = Assert.Single(SessionTranscriptBuilder.Build(Transcript(original), session).Turns);

        Assert.Equal(expected, turn.User.Text);
        Assert.Empty(turn.Assistant);
        Assert.Equal("queued", turn.Status);
        Assert.Equal(hasAttachment ? 1 : 0, session.Status.Inputs![0].Attachments?.Count ?? 0);
        AssertText(SessionTranscriptBuilder.Build(Transcript(original), session, "raw"), original);
    }

    [Fact]
    public void Build_SuppressingCompletedInternalPartDoesNotRenumberLaterVisiblePart()
    {
        const string opening = "<openviking-context>unfinished";
        var data = Transcript(opening);
        data.Parts[1].Text = "ordinary reasoning";
        var before = Assert.Single(SessionTranscriptBuilder.Build(data).Turns).Assistant;
        var laterId = before[1].Id;

        data.Parts[0].Text += "</openviking-context>";
        var after = Assert.Single(SessionTranscriptBuilder.Build(data).Turns).Assistant;
        var raw = Assert.Single(SessionTranscriptBuilder.Build(data, view: "raw").Turns).Assistant;

        Assert.Equal(laterId, Assert.Single(after).Id);
        Assert.Equal("ordinary reasoning", Assert.Single(after).Text);
        Assert.Equal(laterId, raw[1].Id);
        Assert.Equal(opening + "</openviking-context>", raw[0].Text);
    }

    [Fact]
    public void Build_DoesNotPairInternalMarkersAcrossIndependentInputs()
    {
        const string first = "<openviking-context>first ordinary input";
        const string second = "second ordinary input</openviking-context>";
        var session = CanonicalSession(first);
        session.Status = session.Status with
        {
            Inputs = [session.Status.Inputs![0], new AgentSessionInputRecord("input-2", 2, second,
                "web", AgentSessionInputAcceptance.Accepted, At)],
            Turns = [new AgentTurnRecord("canonical-turn-1", 1, ["input-1", "input-2"], AgentTurnStatus.Queued)],
        };

        var turn = Assert.Single(SessionTranscriptBuilder.Build(Transcript("runtime prompt"), session).Turns);

        Assert.Equal(first + "\n" + second, turn.User.Text);
    }

    private static void AssertText(AgentSessionTranscriptResponse response, string expected)
    {
        var turn = Assert.Single(response.Turns);
        Assert.Equal(expected, turn.User.Text);
        Assert.Collection(turn.Assistant,
            text => { Assert.Equal("text", text.Type); Assert.Equal(expected, text.Text); },
            reasoning => { Assert.Equal("reasoning", reasoning.Type); Assert.Equal(expected, reasoning.Text); });
    }

    private static AgentSessionTranscriptData Transcript(string text) => new(
        [new AgentSessionTranscriptTurnRow
        {
            Id = 1, SessionId = "session-1", Sequence = 1, PromptText = text, PromptKind = "task",
            StartedAt = At, UpdatedAt = At,
        }],
        [new AgentSessionTranscriptPartRow
        {
            Id = 1, TurnId = 1, Sequence = 1, Type = "text", Text = text,
            PayloadJson = "{\"role\":\"assistant\"}", FirstSeenAt = At, LastSeenAt = At,
        }, new AgentSessionTranscriptPartRow
        {
            Id = 2, TurnId = 1, Sequence = 2, Type = "reasoning", Text = text,
            PayloadJson = "{\"role\":\"system\"}", FirstSeenAt = At, LastSeenAt = At,
        }]);

    private static AgentSession CanonicalSession(string text, bool hasAttachment = false)
    {
        var session = AgentSession.Create("session-1", "runner-1", "/workspace",
            metadata: new AgentSessionMetadata()
                .WithLabel("mohist.io/project-id", "project-1")
                .WithLabel("mohist.io/source-kind", "workflow")
                .WithLabel("mohist.io/agent-id", "workflow-agent")
                .WithLabel("mohist.io/source-id", "workflow-1")
                .WithLabel("mohist.io/session-name", "transcript"),
            now: At, runtime: "pi");
        session.Status = session.Status with
        {
            Inputs = [new AgentSessionInputRecord("input-1", 1, text, "web",
                AgentSessionInputAcceptance.Accepted, At,
                Attachments: hasAttachment
                    ? [new AgentSessionInputAttachmentDescriptor("attachment-1", "notes.txt", "text/plain", 5, At)]
                    : null)],
            Turns = [new AgentTurnRecord("canonical-turn-1", 1, ["input-1"], AgentTurnStatus.Queued)],
        };
        return session;
    }
}
