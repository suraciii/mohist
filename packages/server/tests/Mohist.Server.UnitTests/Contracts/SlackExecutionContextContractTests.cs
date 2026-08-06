using System.Security.Cryptography;
using System.Text;
using Mohist.Server.Contracts;
using Xunit;

namespace Mohist.Server.UnitTests.Contracts;

public sealed class SlackExecutionContextContractTests
{
    [Fact]
    public void Managed_skill_has_a_stable_identity_hash_and_all_reply_rules()
    {
        var skill = SlackCollaborationSkillCatalog.Resolve();

        Assert.Equal(SlackCollaborationSkillCatalog.Name, skill.Name);
        Assert.Equal(SlackCollaborationSkillCatalog.Version, skill.Version);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(skill.Instructions))).ToLowerInvariant(),
            skill.ContentHash);
        Assert.Contains("You are the speaker", skill.Instructions, StringComparison.Ordinal);
        Assert.Contains("mo slack message send", skill.Instructions, StringComparison.Ordinal);
        Assert.Contains("silence is a legitimate", skill.Instructions, StringComparison.Ordinal);
        Assert.Contains("Do not post empty acknowledgements", skill.Instructions, StringComparison.Ordinal);
        Assert.Contains("@mention the delegator", skill.Instructions, StringComparison.Ordinal);
        Assert.Contains("Never guess a reply destination", skill.Instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void Slack_context_uses_the_thread_root_and_never_allows_a_runner_selected_target()
    {
        var context = SlackExecutionContextFactory.Create(
            "T1", "C1", null, "101.0", "U1", "connection_1", "session_1", "dispatch_1");

        Assert.Equal(AgentSlackExecutionContext.CurrentVersion, context.Version);
        Assert.Equal("101.0", context.ReplyAnchor.ThreadRootMessageId);
        Assert.Equal("101.0", context.ReplyAnchor.TriggeringMessageId);
        Assert.Equal("connection_1", context.ReplyAnchor.ConnectionId);
        Assert.Equal("dispatch_1", context.ReplyAnchor.DispatchRef);
    }
}
