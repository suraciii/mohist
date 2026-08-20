using System.Security.Cryptography;
using System.Text;
using Mohist.Server.Contracts;
using Xunit;

namespace Mohist.Server.UnitTests.Contracts;

public sealed class SlackExecutionContextContractTests
{
    private const string CanonicalInstructions = """
You are the speaker in this Slack conversation. Your reasoning and tool calls are invisible to Slack users; only what you actively send appears as a message.

Send your reply with the Mohist-provided command, reading the destination from the system facts (the Slack reply anchor), never from memory:

  mo slack message send --conversation <conversationId> --reply-to <threadRootMessageId> --text "<your reply>"

- The reply body is rendered in Slack: markdown bold (`**bold**`), inline code (`` `code` ``), fenced code blocks, lists, and quotes display natively; unsupported markdown (tables, headings) degrades to readable plain text. Do not hand-format Slack syntax -- write markdown and let the pipeline render it.
- To include an image, add `--image <public image url>` for a publicly reachable image, or `--file <local image path>` to upload a local screenshot (at most 10 MB). `--text` is optional when an image is attached.
- Send when your turn produced a conclusion, result, or a needed next step. If you have nothing worth saying, send nothing -- silence is a legitimate, normal end of a turn, not a failure.
- A direct human question overrides silence: always answer it, even when the answer is that you have nothing to add. A bare acknowledgement is not an answer.
- When the work failed or needs a human, send the failure reason and the concrete next step yourself. Do not rely on a system template to speak for you.
- Keep replies self-contained: the conclusion, the evidence summary, and the next step all belong in the Slack message. Do not require the user to open another tool to learn the outcome.
- Do not post empty acknowledgements ("got it", "understood", "confirmed"). They disturb the channel and can trigger other bots. Silence is a normal completion, not a failure.
- When you complete delegated work, @mention the delegator in the result message. Mention someone only when they need to act or notice the result; a narrative reference needs no mention.
- Fine-grained progress belongs in the Web session timeline, not in Slack chatter.
- Never guess a reply destination. Use the conversation and reply target from the system facts. Do not target a different channel or an older message from memory.
- Never echo the reply anchor's internal fields (connection id, session id, tokens, member ids) into your reply text.
- After a restart, Session recovery, or context compaction, rebuild state from durable records and the thread and continue silently. Never announce the interruption or ask how to proceed solely because recovery occurred.
"""
        + "\n";

    [Fact]
    public void Managed_skill_matches_the_canonical_fixture_and_integrity_contract()
    {
        var skill = SlackCollaborationSkillCatalog.Resolve();
        var expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalInstructions)))
            .ToLowerInvariant();

        Assert.Equal(SlackCollaborationSkillCatalog.Name, skill.Name);
        Assert.Equal(SlackCollaborationSkillCatalog.Version, skill.Version);
        Assert.Equal(SlackCollaborationSkillCatalog.ContentHash, skill.ContentHash);
        Assert.Equal(CanonicalInstructions, skill.Instructions);
        Assert.Equal(expectedHash, skill.ContentHash);
        Assert.Matches("^[a-f0-9]{64}$", skill.ContentHash);
        Assert.EndsWith("\n", skill.Instructions, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", skill.Instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void Managed_skill_explicitly_defines_all_six_collaboration_rules()
    {
        var skill = SlackCollaborationSkillCatalog.Resolve();

        Assert.Contains("only what you actively send appears as a message", skill.Instructions, StringComparison.Ordinal);
        Assert.Contains("mo slack message send", skill.Instructions, StringComparison.Ordinal);
        Assert.Contains("A direct human question overrides silence", skill.Instructions, StringComparison.Ordinal);
        Assert.Contains("Do not post empty acknowledgements", skill.Instructions, StringComparison.Ordinal);
        Assert.Contains("@mention the delegator", skill.Instructions, StringComparison.Ordinal);
        Assert.Contains("only when they need to act or notice", skill.Instructions, StringComparison.Ordinal);
        Assert.Contains("Keep replies self-contained", skill.Instructions, StringComparison.Ordinal);
        Assert.Contains("conclusion, the evidence summary, and the next step", skill.Instructions, StringComparison.Ordinal);
        Assert.Contains("Fine-grained progress belongs in the Web session timeline", skill.Instructions, StringComparison.Ordinal);
        Assert.Contains("Never guess a reply destination", skill.Instructions, StringComparison.Ordinal);
        Assert.Contains("Never echo the reply anchor's internal fields", skill.Instructions, StringComparison.Ordinal);
        Assert.Contains("After a restart, Session recovery, or context compaction", skill.Instructions, StringComparison.Ordinal);
        Assert.Contains("continue silently", skill.Instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void Same_version_asset_drift_is_rejected_by_the_catalog()
    {
        var changedInstructions = CanonicalInstructions.Replace(
            "A direct human question overrides silence",
            "A direct human question does not override silence",
            StringComparison.Ordinal);

        var error = Assert.Throws<InvalidOperationException>(
            () => SlackCollaborationSkillCatalog.ResolveAssetForTesting(changedInstructions));

        Assert.Contains("drifted", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("does not override silence", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Skill_is_an_instruction_contract_and_does_not_assign_reply_authorship_to_the_server()
    {
        var skill = SlackCollaborationSkillCatalog.Resolve();

        Assert.Contains("only what you actively send appears as a message", skill.Instructions, StringComparison.Ordinal);
        Assert.Contains("Do not rely on a system template to speak for you", skill.Instructions, StringComparison.Ordinal);
        Assert.DoesNotContain("Runtime output", skill.Instructions, StringComparison.Ordinal);
        Assert.DoesNotContain("fallback response", skill.Instructions, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("classify", skill.Instructions, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Slack_context_uses_the_thread_root_and_never_allows_a_runner_selected_target()
    {
        var context = SlackExecutionContextFactory.Create(
            "T1", "C1", "100.0", "101.0", "U1", "connection_1", "session_1", "dispatch_1");

        Assert.Equal(AgentSlackExecutionContext.CurrentVersion, context.Version);
        Assert.Equal("100.0", context.ReplyAnchor.ThreadRootMessageId);
        Assert.Equal("101.0", context.ReplyAnchor.TriggeringMessageId);
        Assert.Equal("connection_1", context.ReplyAnchor.ConnectionId);
        Assert.Equal("dispatch_1", context.ReplyAnchor.DispatchRef);
    }
}
