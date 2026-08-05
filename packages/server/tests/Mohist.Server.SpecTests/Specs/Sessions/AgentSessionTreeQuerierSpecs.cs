using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Specs.Agent.Grain;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

[Collection("AgentJobGrain")]
public sealed class AgentSessionTreeQuerierSpecs
{
    private readonly AgentJobGrainFixture _fixture;

    public AgentSessionTreeQuerierSpecs(AgentJobGrainFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ContinuationPinsOldRevision_AndIncludesEdgeBeforeThatSnapshotDetach()
    {
        var projectId = $"tree-query-{Guid.NewGuid():N}";
        var rootId = $"session-root-{Guid.NewGuid():N}";
        var childId = $"session-child-{Guid.NewGuid():N}";
        var root = _fixture.Grains.GetGrain<IAgentSessionGrain>(rootId);
        var child = _fixture.Grains.GetGrain<IAgentSessionGrain>(childId);
        await root.OpenAsync(new OpenAgentSessionCommand(
            "runner-tree",
            "opencode",
            "/workspace",
            Metadata: Metadata(projectId, "agent-root", "agent-launch")));
        await child.OpenAsync(new OpenAgentSessionCommand(
            string.Empty,
            "pi",
            "/workspace",
            Metadata: Metadata(projectId, "agent-child", "agent-launch"),
            LaunchVisibility: AgentLaunchVisibility.Provisional));

        var fence = _fixture.Grains.GetGrain<ISessionTreeMutationFenceGrain>(projectId);
        await fence.ReserveAsync(LinkCommand(projectId, "edge-1", "command-1", rootId, childId));
        var assigned = await fence.BeginFinalizeAsync("command-1", "edge-1");
        await child.EnsureParentLinkAsync(new EnsureParentLinkCommand(
            new SessionParentLink(
                "edge-1",
                rootId,
                "agent-root",
                "job-1",
                _fixture.TimeProvider.GetUtcNow(),
                assigned.Revision,
                SessionParentLinkState.Attached),
            "/workspace",
            "runner-tree",
            "opencode",
            null));
        await fence.CommitFinalizeAsync("command-1", "edge-1");

        var querier = CreateQuerier();
        var beforePromote = await querier.GetAsync(projectId, rootId, 10, null);
        Assert.NotNull(beforePromote);
        Assert.Equal(1, beforePromote!.Revision);
        Assert.Equal(new[] { rootId, childId }, beforePromote.Nodes.Select(node => node.SessionId).ToArray());
        Assert.Single(beforePromote.Edges);

        var attachedChildRoot = await querier.GetAsync(projectId, childId, 10, null);
        Assert.NotNull(attachedChildRoot);
        Assert.Equal(childId, Assert.Single(attachedChildRoot!.Nodes).SessionId);

        await child.PromoteProvisionalLaunchAsync();
        var afterPromote = await querier.GetAsync(projectId, rootId, 10, null);
        Assert.NotNull(afterPromote);
        Assert.Equal(beforePromote.Revision, afterPromote!.Revision);
        Assert.Equal(
            beforePromote.Nodes.Select(node => node.SessionId).ToArray(),
            afterPromote.Nodes.Select(node => node.SessionId).ToArray());
        Assert.Equal(beforePromote.Edges, afterPromote.Edges);

        var first = await querier.GetAsync(projectId, rootId, 1, null);
        Assert.NotNull(first);
        Assert.Equal(1, first!.Revision);
        Assert.Equal(rootId, first.Root.SessionId);
        Assert.Single(first.Nodes);
        Assert.NotNull(first.Continuation);

        await fence.ReserveAsync(LinkCommand(projectId, "edge-2", "command-2", rootId, $"unused-{Guid.NewGuid():N}"));
        await fence.BeginFinalizeAsync("command-2", "edge-2");
        await fence.CommitFinalizeAsync("command-2", "edge-2");
        await MarkDetachedAsync(childId, 2);

        var continued = await querier.GetAsync(projectId, rootId, 1, first.Continuation);
        Assert.NotNull(continued);
        Assert.Equal(1, continued!.Revision);
        var node = Assert.Single(continued.Nodes);
        Assert.Equal(childId, node.SessionId);
        var edge = Assert.Single(continued.Edges);
        Assert.Equal("edge-1", edge.EdgeId);
        Assert.Equal("attached", edge.State);

        var detachedChildRoot = await querier.GetAsync(projectId, childId, 10, null);
        Assert.NotNull(detachedChildRoot);
        Assert.Equal(childId, Assert.Single(detachedChildRoot!.Nodes).SessionId);
    }

    [Fact]
    public async Task UnlinkedProvisionalSessionIsNotEligibleAsTreeRoot()
    {
        var projectId = $"tree-root-visibility-{Guid.NewGuid():N}";
        var sessionId = $"session-unlinked-provisional-{Guid.NewGuid():N}";
        await _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId).OpenAsync(
            new OpenAgentSessionCommand(
                string.Empty,
                "pi",
                "/workspace",
                Metadata: Metadata(projectId, "agent-provisional", "agent-launch"),
                LaunchVisibility: AgentLaunchVisibility.Provisional));

        var page = await CreateQuerier().GetAsync(projectId, sessionId, 10, null);

        Assert.Null(page);
    }

    [Fact]
    public async Task MalformedOrFutureContinuationIsStableInvalidCursor()
    {
        var projectId = $"tree-cursor-validation-{Guid.NewGuid():N}";
        var rootId = $"session-root-{Guid.NewGuid():N}";
        var root = _fixture.Grains.GetGrain<IAgentSessionGrain>(rootId);
        await root.OpenAsync(new OpenAgentSessionCommand(
            string.Empty,
            "opencode",
            "/workspace",
            Metadata: Metadata(projectId, "agent-root", "agent-launch")));
        var querier = CreateQuerier();

        await Assert.ThrowsAsync<AgentSessionTreeContinuationException>(() =>
            querier.GetAsync(projectId, rootId, 1, "not-base64"));

        var future = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new
            {
                projectId,
                rootSessionId = rootId,
                graphRevision = 1,
                offset = 0,
            },
            JSON.Options)));
        await Assert.ThrowsAsync<AgentSessionTreeContinuationException>(() =>
            querier.GetAsync(projectId, rootId, 1, future));
    }

    [Fact]
    public async Task WatermarkIsMonotonicAndIdempotentAcrossRepeatsAndLowerPublishes()
    {
        var projectId = $"tree-watermark-{Guid.NewGuid():N}";
        var factory = DbFactory();

        Assert.Equal(0, await SessionTreeGraphRevisionWatermark.ReadPublishedRevisionAsync(factory, projectId));

        await SessionTreeGraphRevisionWatermark.PublishAsync(factory, projectId, 3, _fixture.TimeProvider.GetUtcNow());
        await SessionTreeGraphRevisionWatermark.PublishAsync(factory, projectId, 3, _fixture.TimeProvider.GetUtcNow());
        await SessionTreeGraphRevisionWatermark.PublishAsync(factory, projectId, 1, _fixture.TimeProvider.GetUtcNow());

        Assert.Equal(3, await SessionTreeGraphRevisionWatermark.ReadPublishedRevisionAsync(factory, projectId));
    }

    [Fact]
    public async Task CommitFinalizePublishesWatermarkSoQuerierDecouplesFromFenceGrain()
    {
        var projectId = $"tree-publish-{Guid.NewGuid():N}";
        var rootId = $"session-root-{Guid.NewGuid():N}";
        var childId = $"session-child-{Guid.NewGuid():N}";
        await _fixture.Grains.GetGrain<IAgentSessionGrain>(rootId).OpenAsync(new OpenAgentSessionCommand(
            "runner-tree",
            "opencode",
            "/workspace",
            Metadata: Metadata(projectId, "agent-root", "agent-launch")));
        await _fixture.Grains.GetGrain<IAgentSessionGrain>(childId).OpenAsync(new OpenAgentSessionCommand(
            string.Empty,
            "pi",
            "/workspace",
            Metadata: Metadata(projectId, "agent-child", "agent-launch"),
            LaunchVisibility: AgentLaunchVisibility.Provisional));

        var fence = _fixture.Grains.GetGrain<ISessionTreeMutationFenceGrain>(projectId);
        await fence.ReserveAsync(LinkCommand(projectId, "edge-1", "command-1", rootId, childId));
        var assigned = await fence.BeginFinalizeAsync("command-1", "edge-1");
        await _fixture.Grains.GetGrain<IAgentSessionGrain>(childId).EnsureParentLinkAsync(new EnsureParentLinkCommand(
            new SessionParentLink(
                "edge-1",
                rootId,
                "agent-root",
                "job-1",
                _fixture.TimeProvider.GetUtcNow(),
                assigned.Revision,
                SessionParentLinkState.Attached),
            "/workspace",
            "runner-tree",
            "opencode",
            null));
        await fence.CommitFinalizeAsync("command-1", "edge-1");

        Assert.Equal(1, await SessionTreeGraphRevisionWatermark.ReadPublishedRevisionAsync(DbFactory(), projectId));

        var page = await CreateQuerier().GetAsync(projectId, rootId, 10, null);
        Assert.NotNull(page);
        Assert.Equal(1, page!.Revision);
        Assert.Equal(new[] { rootId, childId }, page.Nodes.Select(node => node.SessionId).ToArray());
    }

    [Fact]
    public async Task ReachableMalformedCandidateFailsClosedInsteadOfPartialTree()
    {
        var projectId = $"tree-failclosed-{Guid.NewGuid():N}";
        var rootId = $"session-root-{Guid.NewGuid():N}";
        var childId = $"session-child-{Guid.NewGuid():N}";
        await AttachChildAsync(projectId, rootId, childId, "edge-malformed", "command-malformed", "job-malformed");

        await CorruptRowColumnAsync(childId, row => row.ChildLaunchJobId = null);

        await Assert.ThrowsAsync<SessionTreeProjectionInconsistentException>(() =>
            CreateQuerier().GetAsync(projectId, rootId, 10, null));
    }

    [Fact]
    public async Task UnreachableMalformedCandidateDoesNotFailAReachableTree()
    {
        var projectId = $"tree-unreachable-{Guid.NewGuid():N}";
        var reachableRoot = $"session-root-{Guid.NewGuid():N}";
        var reachableChild = $"session-child-{Guid.NewGuid():N}";
        var orphanRoot = $"session-orphan-{Guid.NewGuid():N}";
        var orphanChild = $"session-orphan-child-{Guid.NewGuid():N}";

        await AttachChildAsync(projectId, reachableRoot, reachableChild, "edge-a", "command-a", "job-a");
        await AttachChildAsync(projectId, orphanRoot, orphanChild, "edge-b", "command-b", "job-b");
        await CorruptRowColumnAsync(orphanChild, row => row.ChildLaunchJobId = null);

        var page = await CreateQuerier().GetAsync(projectId, reachableRoot, 10, null);
        Assert.NotNull(page);
        Assert.Equal(
            new[] { reachableRoot, reachableChild },
            page!.Nodes.Select(node => node.SessionId).ToArray());
    }

    [Fact]
    public async Task ReachableDetachedBeforeAttachedCandidateFailsClosed()
    {
        var projectId = $"tree-detach-order-{Guid.NewGuid():N}";
        var rootId = $"session-root-{Guid.NewGuid():N}";
        var childId = $"session-child-{Guid.NewGuid():N}";
        await AttachChildAsync(projectId, rootId, childId, "edge-bad", "command-bad", "job-bad");

        await CorruptRowColumnAsync(childId, row => row.ParentLinkDetachedRevision = row.ParentLinkAttachedRevision);

        await Assert.ThrowsAsync<SessionTreeProjectionInconsistentException>(() =>
            CreateQuerier().GetAsync(projectId, rootId, 10, null));
    }

    [Fact]
    public async Task FutureAttachedChildIsSkippedAtOldCursorRevisionAndAppearsAtItsOwn()
    {
        var projectId = $"tree-future-attach-{Guid.NewGuid():N}";
        var rootId = $"session-root-{Guid.NewGuid():N}";
        var childId = $"session-child-{Guid.NewGuid():N}";
        var futureChildId = $"session-future-child-{Guid.NewGuid():N}";
        await AttachChildAsync(projectId, rootId, childId, "edge-a", "command-a", "job-a");

        var querier = CreateQuerier();
        var snapshot = await querier.GetAsync(projectId, rootId, 1, null);
        Assert.NotNull(snapshot);
        Assert.Equal(1, snapshot!.Revision);
        Assert.NotNull(snapshot.Continuation);

        await _fixture.Grains.GetGrain<IAgentSessionGrain>(futureChildId).OpenAsync(new OpenAgentSessionCommand(
            string.Empty,
            "pi",
            "/workspace",
            Metadata: Metadata(projectId, "agent-future", "agent-launch"),
            LaunchVisibility: AgentLaunchVisibility.Provisional));
        var fence = _fixture.Grains.GetGrain<ISessionTreeMutationFenceGrain>(projectId);
        await fence.ReserveAsync(LinkCommand(projectId, "edge-b", "command-b", rootId, futureChildId));
        var assigned = await fence.BeginFinalizeAsync("command-b", "edge-b");
        await _fixture.Grains.GetGrain<IAgentSessionGrain>(futureChildId).EnsureParentLinkAsync(new EnsureParentLinkCommand(
            new SessionParentLink(
                "edge-b",
                rootId,
                "agent-root",
                "job-b",
                _fixture.TimeProvider.GetUtcNow(),
                assigned.Revision,
                SessionParentLinkState.Attached),
            "/workspace",
            "runner-tree",
            "opencode",
            null));

        var pinned = await querier.GetAsync(projectId, rootId, 10, snapshot.Continuation);
        Assert.NotNull(pinned);
        Assert.Equal(1, pinned!.Revision);
        Assert.Equal(childId, Assert.Single(pinned.Nodes).SessionId);

        await fence.CommitFinalizeAsync("command-b", "edge-b");

        var current = await querier.GetAsync(projectId, rootId, 10, null);
        Assert.NotNull(current);
        Assert.Equal(2, current!.Revision);
        Assert.Equal(
            new[] { rootId, childId, futureChildId },
            current.Nodes.Select(node => node.SessionId).ToArray());
    }

    [Fact]
    public async Task LegallyDetachedChildIsSkippedInCurrentTree()
    {
        var projectId = $"tree-detach-skip-{Guid.NewGuid():N}";
        var rootId = $"session-root-{Guid.NewGuid():N}";
        var childId = $"session-child-{Guid.NewGuid():N}";
        await AttachChildAsync(projectId, rootId, childId, "edge-d", "command-d", "job-d");

        var fence = _fixture.Grains.GetGrain<ISessionTreeMutationFenceGrain>(projectId);
        await fence.ReserveAsync(LinkCommand(projectId, "edge-x", "command-x", rootId, $"unused-{Guid.NewGuid():N}"));
        await fence.BeginFinalizeAsync("command-x", "edge-x");
        await fence.CommitFinalizeAsync("command-x", "edge-x");
        Assert.Equal(2, await SessionTreeGraphRevisionWatermark.ReadPublishedRevisionAsync(DbFactory(), projectId));

        await MarkDetachedAsync(childId, 2);

        var page = await CreateQuerier().GetAsync(projectId, rootId, 10, null);
        Assert.NotNull(page);
        Assert.Equal(2, page!.Revision);
        Assert.Equal(rootId, Assert.Single(page.Nodes).SessionId);
        Assert.Empty(page.Edges);
    }

    private AgentSessionTreeQuerier CreateQuerier()
    {
        var services = _fixture.Cluster.GetSiloServiceProvider(null);
        return new AgentSessionTreeQuerier(
            services.GetRequiredService<IDbContextFactory<MohistDbContext>>());
    }

    private async Task MarkDetachedAsync(string sessionId, long revision)
    {
        var services = _fixture.Cluster.GetSiloServiceProvider(null);
        var factory = services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var row = await db.AgentSessions.SingleAsync(item => item.Id == sessionId);
        var session = AgentSessionJson.Deserialize(row)!;
        session.ParentLink = session.ParentLink! with
        {
            State = SessionParentLinkState.Detached,
            DetachedRevision = revision,
            DetachedAt = _fixture.TimeProvider.GetUtcNow(),
        };
        row.State = JsonSerializer.Serialize(session, JSON.Options);
        row.ParentLinkState = "detached";
        row.ParentLinkDetachedRevision = revision;
        row.ParentLinkDetachedAt = session.ParentLink.DetachedAt?.ToString("O");
        await db.SaveChangesAsync();
    }

    private IDbContextFactory<MohistDbContext> DbFactory() =>
        _fixture.Cluster.GetSiloServiceProvider(null).GetRequiredService<IDbContextFactory<MohistDbContext>>();

    private async Task AttachChildAsync(
        string projectId, string rootId, string childId, string edgeId, string commandId, string jobId)
    {
        await _fixture.Grains.GetGrain<IAgentSessionGrain>(rootId).OpenAsync(new OpenAgentSessionCommand(
            "runner-tree",
            "opencode",
            "/workspace",
            Metadata: Metadata(projectId, "agent-root", "agent-launch")));
        await _fixture.Grains.GetGrain<IAgentSessionGrain>(childId).OpenAsync(new OpenAgentSessionCommand(
            string.Empty,
            "pi",
            "/workspace",
            Metadata: Metadata(projectId, "agent-child", "agent-launch"),
            LaunchVisibility: AgentLaunchVisibility.Provisional));
        var fence = _fixture.Grains.GetGrain<ISessionTreeMutationFenceGrain>(projectId);
        await fence.ReserveAsync(LinkCommand(projectId, edgeId, commandId, rootId, childId));
        var assigned = await fence.BeginFinalizeAsync(commandId, edgeId);
        await _fixture.Grains.GetGrain<IAgentSessionGrain>(childId).EnsureParentLinkAsync(new EnsureParentLinkCommand(
            new SessionParentLink(
                edgeId,
                rootId,
                "agent-root",
                jobId,
                _fixture.TimeProvider.GetUtcNow(),
                assigned.Revision,
                SessionParentLinkState.Attached),
            "/workspace",
            "runner-tree",
            "opencode",
            null));
        await fence.CommitFinalizeAsync(commandId, edgeId);
    }

    private async Task CorruptRowColumnAsync(string sessionId, Action<AgentSessionRow> mutate)
    {
        await using var db = await DbFactory().CreateDbContextAsync();
        var row = await db.AgentSessions.SingleAsync(item => item.Id == sessionId);
        mutate(row);
        await db.SaveChangesAsync();
    }

    private static AgentSessionMetadata Metadata(string projectId, string agentId, string source) =>
        new(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
            [AgentSessionQueryMetadataKeys.SourceKind] = source,
            [GenericAgentSessionMetadata.AgentId] = agentId,
            [GenericAgentSessionMetadata.AgentName] = agentId,
        });

    private static ReserveSessionTreeLinkCommand LinkCommand(
        string projectId,
        string edgeId,
        string commandId,
        string parentSessionId,
        string childSessionId) =>
        new(
            projectId,
            edgeId,
            parentSessionId,
            childSessionId,
            "/workspace",
            "runner-tree",
            "opencode",
            null,
            commandId);
}
