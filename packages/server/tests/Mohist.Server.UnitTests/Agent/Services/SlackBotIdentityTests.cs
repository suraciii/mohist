using Mohist.Server.Agent.Services;
using Xunit;
using DomainAgent = Mohist.Server.Agent.Domain.Agent;

namespace Mohist.Server.UnitTests.Agent.Services;

public sealed class SlackBotIdentityTests
{
    [Fact]
    public void Valid_name_and_description_are_used_as_is()
    {
        var agent = NewAgent("agent-valid", "release_helper.2", "Reviews release changes.");

        var preview = SlackBotIdentityDeriver.Derive(agent);

        Assert.Equal("release_helper.2", preview.BotName);
        Assert.Equal("Reviews release changes.", preview.AppDescription);
    }

    [Fact]
    public void Invalid_name_is_sanitized_with_a_stable_Agent_suffix_without_mutating_the_Agent()
    {
        var agent = NewAgent("agent-stable", "Release Helper!", "Keeps releases moving.");
        var originalName = agent.Name;
        var originalDescription = agent.Description;

        var first = SlackBotIdentityDeriver.Derive(agent);
        var second = SlackBotIdentityDeriver.Derive(agent);
        var otherAgent = SlackBotIdentityDeriver.Derive(
            NewAgent("agent-other", agent.Name, agent.Description));

        Assert.Equal(first, second);
        Assert.Matches("^release-helper-[0-9a-f]{8}$", first.BotName);
        Assert.Matches("^[a-z0-9._-]{1,80}$", first.BotName);
        Assert.NotEqual(first.BotName, otherAgent.BotName);
        Assert.Equal(originalName, agent.Name);
        Assert.Equal(originalDescription, agent.Description);
    }

    [Fact]
    public void Blank_name_and_description_receive_non_empty_fallbacks()
    {
        var preview = SlackBotIdentityDeriver.Derive(NewAgent("agent-blank", " \t", " \n"));

        Assert.Matches("^agent-[0-9a-f]{8}$", preview.BotName);
        Assert.False(string.IsNullOrWhiteSpace(preview.AppDescription));
    }

    [Fact]
    public void Invalid_long_name_is_capped_within_Slack_limit_after_suffixing()
    {
        var preview = SlackBotIdentityDeriver.Derive(
            NewAgent("agent-long", new string('a', 81), "Long-running helper."));

        Assert.Equal(80, preview.BotName.Length);
        Assert.Matches("^[a-z0-9._-]{1,80}$", preview.BotName);
        Assert.Matches("-[0-9a-f]{8}$", preview.BotName);
    }

    private static DomainAgent NewAgent(string id, string name, string description) => new()
    {
        Id = id,
        ProjectId = "project-1",
        Name = name,
        Description = description,
    };
}
