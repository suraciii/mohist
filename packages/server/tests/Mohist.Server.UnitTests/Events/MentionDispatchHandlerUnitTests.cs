using Mohist.Server.Agent.Services;
using Mohist.Server.Events.Subscriptions;
using Mohist.Server.Sessions.Services;
using Xunit;
using AgentDomain = Mohist.Server.Agent.Domain;

namespace Mohist.Server.UnitTests.Events;

/// <summary>
/// issue-490 T-002 — unit coverage for the comment-anchored stable-key
/// derivation on <see cref="AgentSessionResolver"/> (design D3) and the
/// pure helper methods on <see cref="MentionDispatchHandler"/> (loop
/// prevention, name index). The end-to-end comment→launch behavior is
/// covered by <c>CommentMentionDispatchSpecs</c>; these unit tests pin the
/// key-derivation contract and helper edge cases without a database.
/// </summary>
public class MentionDispatchHandlerUnitTests
{
    [Fact]
    public void CommentSessionId_IsStableForSameInputs()
    {
        var resolver = CreateResolver();

        var first = resolver.CommentSessionId("proj_x", "cmt_1", "agent_a");
        var second = resolver.CommentSessionId("proj_x", "cmt_1", "agent_a");

        Assert.Equal(first, second);
    }

    [Fact]
    public void CommentSessionId_DistinctForDifferentComments()
    {
        var resolver = CreateResolver();

        var first = resolver.CommentSessionId("proj_x", "cmt_1", "agent_a");
        var second = resolver.CommentSessionId("proj_x", "cmt_2", "agent_a");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void CommentSessionId_DistinctForDifferentAgents()
    {
        var resolver = CreateResolver();

        var first = resolver.CommentSessionId("proj_x", "cmt_1", "agent_a");
        var second = resolver.CommentSessionId("proj_x", "cmt_1", "agent_b");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void CommentSessionId_DistinctForDifferentProjects()
    {
        var resolver = CreateResolver();

        var first = resolver.CommentSessionId("proj_x", "cmt_1", "agent_a");
        var second = resolver.CommentSessionId("proj_y", "cmt_1", "agent_a");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void CommentSessionId_And_CommentJobKey_AreDifferentShapes()
    {
        var resolver = CreateResolver();

        var sessionId = resolver.CommentSessionId("proj_x", "cmt_1", "agent_a");
        var jobKey = resolver.CommentJobKey("proj_x", "cmt_1", "agent_a");

        Assert.NotEqual(sessionId, jobKey);
        Assert.StartsWith("agent-session-", sessionId);
        Assert.StartsWith("agent-job-trigger-", jobKey);
    }

    [Fact]
    public void CommentSessionId_DiffersFromRoutedStableSessionId()
    {
        // Comment-anchored and routed keys must NOT collide — they live in
        // the same grain-key namespace. Same project + agent, different
        // anchor (commentId vs eventId+ruleId) must produce distinct keys
        // even when the anchor strings happen to be equal.
        var resolver = CreateResolver();

        var commentSession = resolver.CommentSessionId("proj_x", "anchor", "agent_a");
        var routedSession = resolver.StableSessionId("proj_x", "anchor", "rule");

        Assert.NotEqual(commentSession, routedSession);
    }

    [Fact]
    public void CommentJobKey_DiffersFromRoutedStableJobKey()
    {
        var resolver = CreateResolver();

        var commentJob = resolver.CommentJobKey("proj_x", "anchor", "agent_a");
        var routedJob = resolver.StableJobKey("proj_x", "anchor", "rule");

        Assert.NotEqual(commentJob, routedJob);
    }

    [Fact]
    public void IsAuthoredByActiveAgent_ReturnsFalseForEmptyAuthor()
    {
        Assert.False(MentionDispatchHandler.IsAuthoredByActiveAgent("", []));
        Assert.False(MentionDispatchHandler.IsAuthoredByActiveAgent("   ", []));
    }

    [Fact]
    public void IsAuthoredByActiveAgent_ReturnsFalseForEmptyAgentList()
    {
        Assert.False(MentionDispatchHandler.IsAuthoredByActiveAgent("supervisor", []));
    }

    [Fact]
    public void IsAuthoredByActiveAgent_MatchesCaseInsensitively()
    {
        var agents = new[]
        {
            NewAgentInfo("agent_1", "supervisor"),
            NewAgentInfo("agent_2", "coder"),
        };

        Assert.True(MentionDispatchHandler.IsAuthoredByActiveAgent("supervisor", agents));
        Assert.True(MentionDispatchHandler.IsAuthoredByActiveAgent("SuperVisor", agents));
        Assert.True(MentionDispatchHandler.IsAuthoredByActiveAgent("SUPERVISOR", agents));
        Assert.True(MentionDispatchHandler.IsAuthoredByActiveAgent("coder", agents));
        Assert.False(MentionDispatchHandler.IsAuthoredByActiveAgent("reviewer", agents));
    }

    [Fact]
    public void BuildActiveAgentNameIndex_MapsNamesCaseInsensitively()
    {
        var agents = new[]
        {
            NewAgentInfo("agent_1", "supervisor"),
            NewAgentInfo("agent_2", "coder"),
        };

        var index = MentionDispatchHandler.BuildActiveAgentNameIndex(agents, StringComparer.OrdinalIgnoreCase);

        Assert.Equal("agent_1", index["supervisor"].Id);
        Assert.Equal("agent_1", index["SUPERVISOR"].Id);
        Assert.Equal("agent_2", index["coder"].Id);
    }

    private static AgentSessionResolver CreateResolver() =>
        new(query: null!, grains: null!);

    private static AgentInfo NewAgentInfo(string id, string name) => new(
        Id: id,
        ProjectId: "proj_test",
        Name: name,
        Description: string.Empty,
        Instructions: string.Empty,
        AgentConfig: null,
        Skills: Array.Empty<string>(),
        MaxConcurrentRuns: null,
        Status: AgentDomain.AgentStatus.Active,
        CreatedAt: "2026-07-25T00:00:00Z",
        UpdatedAt: "2026-07-25T00:00:00Z");
}
