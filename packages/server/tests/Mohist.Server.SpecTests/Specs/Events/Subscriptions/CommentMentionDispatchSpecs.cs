using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Subscriptions;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Events.Subscriptions;

/// <summary>
/// issue-490 T-002 — Spec scenarios for <c>comment-mention</c>. Each spec
/// seeds the project / Agent rows directly, drives the production
/// <see cref="MentionDispatchHandler"/> with a single
/// <c>com.mohist.issue.comment-added</c> CloudEvent, and inspects the captured
/// <see cref="RecordingAgentLauncher.MentionLaunches"/> log. No real network /
/// grain / Orleans is touched (design/testing.md hard constraint 1).
///
/// <para>
/// The handler under test is the workspace-optional manual launch path
/// (design Decision 1). The launcher fake captures each mention launch
/// without consulting a workflow run / workspace, so a mention fires
/// regardless of issue state — exactly the contract the spec verifies.
/// </para>
///
/// Spec: <c>openspec/changes/issue-490/specs/comment-mention/spec.md</c>.
/// </summary>
public sealed class CommentMentionDispatchSpecs
{
    [Fact]
    public async Task MentionOfActiveAgent_LaunchesItWithFullCommentBodyAsPrompt()
    {
        var harness = await SeedAsync("mention-one");

        var evt = RoutingDispatchTestSupport.BuildCommentAddedEvent(
            projectId: harness.ProjectId,
            issueNumber: harness.IssueNumber,
            eventId: "evt-mention-1",
            commentId: "cmt-1",
            author: "Ada",
            body: "@supervisor push this issue forward");

        await harness.MentionHandler.HandleAsync(evt, CancellationToken.None);

        var launch = Assert.Single(harness.Launcher.MentionLaunches);
        Assert.Equal(harness.SupervisorId, launch.AgentId);
        Assert.Equal("supervisor", launch.AgentName);
        Assert.Equal("@supervisor push this issue forward", launch.Prompt);
        Assert.Equal("cmt-1", launch.CommentId);
        Assert.Equal("evt-mention-1", launch.TriggeringEventId);
        Assert.Equal(harness.ProjectId, launch.ProjectId);
        Assert.Equal(harness.IssueNumber, launch.IssueNumber);
    }

    [Fact]
    public async Task Mention_PreservesAtTokenInPrompt_Verbatim()
    {
        var harness = await SeedAsync("mention-verbatim");

        var evt = RoutingDispatchTestSupport.BuildCommentAddedEvent(
            projectId: harness.ProjectId,
            issueNumber: harness.IssueNumber,
            eventId: "evt-verbatim",
            commentId: "cmt-verbatim",
            author: "Ada",
            body: "@supervisor, please help.");

        await harness.MentionHandler.HandleAsync(evt, CancellationToken.None);

        var launch = Assert.Single(harness.Launcher.MentionLaunches);
        Assert.Equal("@supervisor, please help.", launch.Prompt);
    }

    [Fact]
    public async Task CommentWithoutMention_LaunchesNothing()
    {
        var harness = await SeedAsync("no-mention");

        var evt = RoutingDispatchTestSupport.BuildCommentAddedEvent(
            projectId: harness.ProjectId,
            issueNumber: harness.IssueNumber,
            eventId: "evt-no-mention",
            commentId: "cmt-no-mention",
            author: "Ada",
            body: "Looks good, no ping.");

        await harness.MentionHandler.HandleAsync(evt, CancellationToken.None);

        Assert.Empty(harness.Launcher.MentionLaunches);
    }

    [Fact]
    public async Task Mention_IsDelimitedByPunctuation()
    {
        var harness = await SeedAsync("mention-punct");

        var evt = RoutingDispatchTestSupport.BuildCommentAddedEvent(
            projectId: harness.ProjectId,
            issueNumber: harness.IssueNumber,
            eventId: "evt-punct",
            commentId: "cmt-punct",
            author: "Ada",
            body: "ping @supervisor.");

        await harness.MentionHandler.HandleAsync(evt, CancellationToken.None);

        var launch = Assert.Single(harness.Launcher.MentionLaunches);
        Assert.Equal("supervisor", launch.AgentName);
    }

    [Fact]
    public async Task Mention_MatchingIsCaseInsensitive()
    {
        var harness = await SeedAsync("mention-case");

        var evt = RoutingDispatchTestSupport.BuildCommentAddedEvent(
            projectId: harness.ProjectId,
            issueNumber: harness.IssueNumber,
            eventId: "evt-case",
            commentId: "cmt-case",
            author: "Ada",
            body: "@SuperVisor please");

        await harness.MentionHandler.HandleAsync(evt, CancellationToken.None);

        var launch = Assert.Single(harness.Launcher.MentionLaunches);
        Assert.Equal(harness.SupervisorId, launch.AgentId);
        Assert.Equal("supervisor", launch.AgentName);
    }

    [Fact]
    public async Task RepeatedMentionOfSameAgent_LaunchesOnce()
    {
        var harness = await SeedAsync("mention-repeat");

        var evt = RoutingDispatchTestSupport.BuildCommentAddedEvent(
            projectId: harness.ProjectId,
            issueNumber: harness.IssueNumber,
            eventId: "evt-repeat",
            commentId: "cmt-repeat",
            author: "Ada",
            body: "@supervisor @supervisor @supervisor please");

        await harness.MentionHandler.HandleAsync(evt, CancellationToken.None);

        var launch = Assert.Single(harness.Launcher.MentionLaunches);
        Assert.Equal("supervisor", launch.AgentName);
    }

    [Fact]
    public async Task DistinctMentions_EachLaunchIndependently()
    {
        var harness = await SeedAsync("mention-distinct");
        await RoutingDispatchTestSupport.SeedNamedAgentAsync(
            harness.Database, harness.ProjectId, "agent_coder", "coder");

        var evt = RoutingDispatchTestSupport.BuildCommentAddedEvent(
            projectId: harness.ProjectId,
            issueNumber: harness.IssueNumber,
            eventId: "evt-distinct",
            commentId: "cmt-distinct",
            author: "Ada",
            body: "@supervisor and @coder please coordinate");

        await harness.MentionHandler.HandleAsync(evt, CancellationToken.None);

        Assert.Equal(2, harness.Launcher.MentionLaunchCount);
        var names = harness.Launcher.MentionLaunches.Select(l => l.AgentName).ToHashSet();
        Assert.Contains("supervisor", names);
        Assert.Contains("coder", names);
        Assert.All(harness.Launcher.MentionLaunches, launch =>
        {
            Assert.Equal("cmt-distinct", launch.CommentId);
            Assert.Equal("evt-distinct", launch.TriggeringEventId);
            Assert.Equal("@supervisor and @coder please coordinate", launch.Prompt);
        });
    }

    [Fact]
    public async Task CommentAuthoredByActiveAgent_NeverScanned_LoopPrevention()
    {
        var harness = await SeedAsync("loop-prevention");

        // The supervisor Agent authored this comment (--author supervisor).
        var evt = RoutingDispatchTestSupport.BuildCommentAddedEvent(
            projectId: harness.ProjectId,
            issueNumber: harness.IssueNumber,
            eventId: "evt-loop",
            commentId: "cmt-loop",
            author: "supervisor",
            body: "I tried @coder but it should not fire from my own comment.");

        await harness.MentionHandler.HandleAsync(evt, CancellationToken.None);

        Assert.Empty(harness.Launcher.MentionLaunches);
    }

    [Fact]
    public async Task CommentAuthoredByActiveAgent_LoopPreventionIsCaseInsensitive()
    {
        var harness = await SeedAsync("loop-case");

        var evt = RoutingDispatchTestSupport.BuildCommentAddedEvent(
            projectId: harness.ProjectId,
            issueNumber: harness.IssueNumber,
            eventId: "evt-loop-case",
            commentId: "cmt-loop-case",
            author: "SuperVisor",
            body: "@coder please");

        await harness.MentionHandler.HandleAsync(evt, CancellationToken.None);

        Assert.Empty(harness.Launcher.MentionLaunches);
    }

    [Fact]
    public async Task HumanAuthoredComment_TriggersNormally()
    {
        var harness = await SeedAsync("human-triggers");

        var evt = RoutingDispatchTestSupport.BuildCommentAddedEvent(
            projectId: harness.ProjectId,
            issueNumber: harness.IssueNumber,
            eventId: "evt-human",
            commentId: "cmt-human",
            author: "Ada Lovelace",
            body: "@supervisor please");

        await harness.MentionHandler.HandleAsync(evt, CancellationToken.None);

        Assert.Single(harness.Launcher.MentionLaunches);
    }

    [Fact]
    public async Task UnknownMention_LaunchesNothing()
    {
        var harness = await SeedAsync("unknown-mention");

        var evt = RoutingDispatchTestSupport.BuildCommentAddedEvent(
            projectId: harness.ProjectId,
            issueNumber: harness.IssueNumber,
            eventId: "evt-unknown",
            commentId: "cmt-unknown",
            author: "Ada",
            body: "@nonexistent please");

        await harness.MentionHandler.HandleAsync(evt, CancellationToken.None);

        Assert.Empty(harness.Launcher.MentionLaunches);
    }

    [Fact]
    public async Task MentionMatchingArchivedAgent_LaunchesNothing()
    {
        var harness = await SeedAsync("archived-mention");
        await RoutingDispatchTestSupport.SeedNamedAgentAsync(
            harness.Database, harness.ProjectId, "agent_archived", "archived-one", status: AgentStatus.Archived);

        var evt = RoutingDispatchTestSupport.BuildCommentAddedEvent(
            projectId: harness.ProjectId,
            issueNumber: harness.IssueNumber,
            eventId: "evt-archived",
            commentId: "cmt-archived",
            author: "Ada",
            body: "@archived-one please");

        await harness.MentionHandler.HandleAsync(evt, CancellationToken.None);

        Assert.Empty(harness.Launcher.MentionLaunches);
    }

    [Fact]
    public async Task RedeliveryOfSameComment_LaunchStaysAnchoredOnCommentIdentity()
    {
        var harness = await SeedAsync("redelivery");

        var evt = RoutingDispatchTestSupport.BuildCommentAddedEvent(
            projectId: harness.ProjectId,
            issueNumber: harness.IssueNumber,
            eventId: "evt-redeliver",
            commentId: "cmt-redeliver",
            author: "Ada",
            body: "@supervisor please");

        await harness.MentionHandler.HandleAsync(evt, CancellationToken.None);
        await harness.MentionHandler.HandleAsync(evt, CancellationToken.None);

        // The recording fake logs every handler invocation, so two deliveries
        // produce two captured entries. The at-most-once guarantee lives in
        // the launcher's comment-anchored stable key (projectId, commentId,
        // agentId) — the spec asserts the (commentId, agentId) pairing is
        // identical across deliveries so the production grain key would
        // collide and the second delivery is a no-op at the AgentJob grain.
        Assert.Equal(2, harness.Launcher.MentionLaunchCount);
        foreach (var launch in harness.Launcher.MentionLaunches)
        {
            Assert.Equal("cmt-redeliver", launch.CommentId);
            Assert.Equal(harness.SupervisorId, launch.AgentId);
        }
    }

    [Fact]
    public async Task DistinctComments_MentioningSameAgent_EachLaunch()
    {
        var harness = await SeedAsync("distinct-comments");

        var first = RoutingDispatchTestSupport.BuildCommentAddedEvent(
            projectId: harness.ProjectId,
            issueNumber: harness.IssueNumber,
            eventId: "evt-comment-1",
            commentId: "cmt-A",
            author: "Ada",
            body: "@supervisor first");
        var second = RoutingDispatchTestSupport.BuildCommentAddedEvent(
            projectId: harness.ProjectId,
            issueNumber: harness.IssueNumber,
            eventId: "evt-comment-2",
            commentId: "cmt-B",
            author: "Ada",
            body: "@supervisor second");

        await harness.MentionHandler.HandleAsync(first, CancellationToken.None);
        await harness.MentionHandler.HandleAsync(second, CancellationToken.None);

        Assert.Equal(2, harness.Launcher.MentionLaunchCount);
        Assert.Equal("cmt-A", harness.Launcher.MentionLaunches[0].CommentId);
        Assert.Equal("cmt-B", harness.Launcher.MentionLaunches[1].CommentId);
    }

    [Fact]
    public async Task Mention_OnBacklogIssue_LaunchesWithoutPreflight()
    {
        // Backlog issue: no workflow run seeded (only the Agent + project).
        // The mention must still fire — the manual launch path is
        // workspace-optional and applies no preflight gate (design Decision 1,
        // spec <i>Mention launches on a backlog issue</i>).
        var database = RoutingDispatchTestSupport.CreateDatabase();
        const string projectId = "proj-backlog";
        const int issueNumber = 7;
        await RoutingDispatchTestSupport.SeedNamedAgentAsync(database, projectId, "agent_supervisor", "supervisor");

        var launcher = new RecordingAgentLauncher();
        var scopeFactory = RoutingDispatchTestSupport.CreateScopeFactory(database, launcher);
        var handler = RoutingDispatchTestSupport.CreateMentionHandler(scopeFactory);

        var evt = RoutingDispatchTestSupport.BuildCommentAddedEvent(
            projectId: projectId,
            issueNumber: issueNumber,
            eventId: "evt-backlog",
            commentId: "cmt-backlog",
            author: "Ada",
            body: "@supervisor start this backlog issue");

        await handler.HandleAsync(evt, CancellationToken.None);

        Assert.Single(launcher.MentionLaunches);
    }

    [Fact]
    public async Task Mention_StampsEpicLineageOnLaunchContext()
    {
        var harness = await SeedAsync("epic-lineage");

        var evt = RoutingDispatchTestSupport.BuildCommentAddedEvent(
            projectId: harness.ProjectId,
            issueNumber: harness.IssueNumber,
            eventId: "evt-epic",
            commentId: "cmt-epic",
            author: "Ada",
            body: "@supervisor epic-stamped",
            epicNumber: 42);

        await harness.MentionHandler.HandleAsync(evt, CancellationToken.None);

        var launch = Assert.Single(harness.Launcher.MentionLaunches);
        Assert.Equal(42, launch.EpicNumber);
        Assert.Equal(harness.IssueNumber, launch.IssueNumber);
    }

    [Fact]
    public async Task MutedAgent_StillLaunchesWhenExplicitlyMentioned()
    {
        // Decision 7: an explicit @ overrides mute. The mention handler
        // does NOT consult WatchEntryStore, so a muted Agent on this issue
        // is still launched when a human @-mentions it. This spec seeds a
        // muted watch entry directly (bypassing the store's active-Agent
        // validation) and asserts the mention still fires.
        var harness = await SeedAsync("mute-override");
        await RoutingDispatchTestSupport.SeedWatchEntryRawAsync(
            harness.Database, harness.ProjectId, harness.IssueNumber, harness.SupervisorId, WatchEntryState.Muted);

        var evt = RoutingDispatchTestSupport.BuildCommentAddedEvent(
            projectId: harness.ProjectId,
            issueNumber: harness.IssueNumber,
            eventId: "evt-muted",
            commentId: "cmt-muted",
            author: "Ada",
            body: "@supervisor explicit override");

        await harness.MentionHandler.HandleAsync(evt, CancellationToken.None);

        var launch = Assert.Single(harness.Launcher.MentionLaunches);
        Assert.Equal(harness.SupervisorId, launch.AgentId);
    }

    private static async Task<MentionHarness> SeedAsync(string purpose)
    {
        var projectId = $"proj-mention-{purpose}";
        const int issueNumber = 42;
        var supervisorId = $"agent_supervisor_{purpose}";

        var database = RoutingDispatchTestSupport.CreateDatabase();
        await RoutingDispatchTestSupport.SeedNamedAgentAsync(database, projectId, supervisorId, "supervisor");
        await RoutingDispatchTestSupport.SeedIssueWithRunAsync(database, projectId, issueNumber, $"wf_{purpose}");

        var launcher = new RecordingAgentLauncher();
        var scopeFactory = RoutingDispatchTestSupport.CreateScopeFactory(database, launcher);
        var handler = RoutingDispatchTestSupport.CreateMentionHandler(scopeFactory);

        return new MentionHarness(database, scopeFactory, handler, launcher, projectId, issueNumber, supervisorId);
    }

    private sealed record MentionHarness(
        TestSqliteDatabase Database,
        IServiceScopeFactory ScopeFactory,
        MentionDispatchHandler MentionHandler,
        RecordingAgentLauncher Launcher,
        string ProjectId,
        int IssueNumber,
        string SupervisorId);
}
