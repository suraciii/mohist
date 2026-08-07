using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Grain;

/// <summary>
/// Managed-worktree spawn state machine: the coordinator persists
/// <see cref="MaterializeState"/> on the durable plan and drives
/// materialize/release through <see cref="IAgentWorkspaceMaterializer"/>.
/// Driven directly at the coordinator grain (admission is bypassed) so the
/// durable recovery/abort semantics are exercised without a real network or
/// filesystem.
/// </summary>
[Collection("AgentSpawnCoordinator")]
public sealed class AgentWorkspaceSpawnSpecs : AgentJobGrainTestSupport
{
    public AgentWorkspaceSpawnSpecs(AgentJobGrainFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task InheritSpawn_NeverCallsMaterializer_ChildWorkDirIsParent()
    {
        var (runnerId, projectId, parentSessionId) = await SetupParentAsync();
        var childSessionId = $"wt-inherit-child-{Guid.NewGuid():N}";
        var idempotencyKey = $"wt-inherit-key-{Guid.NewGuid():N}";
        const string targetAgentId = "agent-inherit-target";
        const string prompt = "inherit spawn";

        await LaunchAsync(projectId, parentSessionId, childSessionId, idempotencyKey,
            targetAgentId, prompt, runnerId, workDir: "/workspace/parent",
            workspaceMode: null, repository: null);

        Assert.Empty(_fixture.AgentWorkspaceMaterializer.MaterializeCalls);
        await AssertChildWorkDirAsync(projectId, childSessionId, "/workspace/parent");
    }

    [Fact]
    public async Task WorktreeSpawn_Materializes_ChildWorkDirAndSourceConfirmed()
    {
        var (runnerId, projectId, parentSessionId) = await SetupParentAsync();
        var childSessionId = $"wt-ok-child-{Guid.NewGuid():N}";
        var idempotencyKey = $"wt-ok-key-{Guid.NewGuid():N}";
        const string targetAgentId = "agent-wt-target";
        const string prompt = "worktree spawn";
        var repository = new WorkspaceRepositorySnapshot("main", "https://example/repo.git", "main");

        _fixture.AgentWorkspaceMaterializer.Reset();
        await LaunchAsync(projectId, parentSessionId, childSessionId, idempotencyKey,
            targetAgentId, prompt, runnerId, workDir: "/workspace/parent",
            workspaceMode: WorkspaceMode.Worktree, repository: repository);

        var materialize = Assert.Single(_fixture.AgentWorkspaceMaterializer.MaterializeCalls);
        Assert.Equal(childSessionId, materialize.ChildSessionId);
        Assert.Equal("/workspace/parent", materialize.ParentWorkDir);
        Assert.Equal("main", materialize.RepositoryName);
        Assert.Empty(_fixture.AgentWorkspaceMaterializer.ReleaseCalls);

        await AssertChildWorkDirAsync(projectId, childSessionId,
            $"/runner-root/agent-workspaces/{childSessionId}");
        var session = await GetSessionAsync(projectId, childSessionId);
        Assert.NotNull(session!.WorkspaceRepository);
        Assert.Equal(WorkspaceRepositoryState.Confirmed, session.WorkspaceRepository!.State);
    }

    [Fact]
    public async Task WorktreeSpawn_RejectedIsDurablePostPlanRejection_NoChild()
    {
        var (runnerId, projectId, parentSessionId) = await SetupParentAsync();
        var childSessionId = $"wt-rej-child-{Guid.NewGuid():N}";
        var idempotencyKey = $"wt-rej-key-{Guid.NewGuid():N}";
        const string targetAgentId = "agent-wt-rej-target";
        const string prompt = "worktree rejected";
        var repository = new WorkspaceRepositorySnapshot("main", "https://example/repo.git", "main");

        _fixture.AgentWorkspaceMaterializer.Reset();
        _fixture.AgentWorkspaceMaterializer.MaterializeOutcome = _ => new MaterializeAgentWorkspaceResult(
            AgentWorkspaceMaterializeOutcome.Rejected, Reason: MaterializeRejectionReason.Capacity);

        await Assert.ThrowsAsync<AgentSpawnPostPlanRejectedException>(() =>
            LaunchAsync(projectId, parentSessionId, childSessionId, idempotencyKey,
                targetAgentId, prompt, runnerId, workDir: "/workspace/parent",
                workspaceMode: WorkspaceMode.Worktree, repository: repository));

        // durable replay: same key returns the same rejection, no second worktree.
        await Assert.ThrowsAsync<AgentSpawnPostPlanRejectedException>(() =>
            LaunchAsync(projectId, parentSessionId, childSessionId, idempotencyKey,
                targetAgentId, prompt, runnerId, workDir: "/workspace/parent",
                workspaceMode: WorkspaceMode.Worktree, repository: repository));

        Assert.Empty(await SessionsByIdsAsync(projectId, [childSessionId]));
    }

    [Fact]
    public async Task WorktreeSpawn_UnknownStaysRequested_ThenReplayMaterializes()
    {
        var (runnerId, projectId, parentSessionId) = await SetupParentAsync();
        var childSessionId = $"wt-unk-child-{Guid.NewGuid():N}";
        var idempotencyKey = $"wt-unk-key-{Guid.NewGuid():N}";
        const string targetAgentId = "agent-wt-unk-target";
        const string prompt = "worktree unknown";
        var repository = new WorkspaceRepositorySnapshot("main", "https://example/repo.git", "main");

        _fixture.AgentWorkspaceMaterializer.Reset();
        _fixture.AgentWorkspaceMaterializer.MaterializeOutcome = _ =>
            MaterializeAgentWorkspaceResult.Unknown;

        await Assert.ThrowsAsync<LaunchSetupPendingException>(() =>
            LaunchAsync(projectId, parentSessionId, childSessionId, idempotencyKey,
                targetAgentId, prompt, runnerId, workDir: "/workspace/parent",
                workspaceMode: WorkspaceMode.Worktree, repository: repository));
        Assert.Single(_fixture.AgentWorkspaceMaterializer.MaterializeCalls);

        // plan stayed Requested, no child yet; replay converges once the Runner answers.
        Assert.Empty(await SessionsByIdsAsync(projectId, [childSessionId]));

        _fixture.AgentWorkspaceMaterializer.MaterializeOutcome = null;
        await LaunchAsync(projectId, parentSessionId, childSessionId, idempotencyKey,
            targetAgentId, prompt, runnerId, workDir: "/workspace/parent",
            workspaceMode: WorkspaceMode.Worktree, repository: repository);

        await AssertChildWorkDirAsync(projectId, childSessionId,
            $"/runner-root/agent-workspaces/{childSessionId}");
    }

    private async Task<(string RunnerId, string ProjectId, string ParentSessionId)> SetupParentAsync()
    {
        var runnerId = $"wt-runner-{Guid.NewGuid():N}";
        var projectId = $"wt-project-{Guid.NewGuid():N}";
        var (_, _) = await RegisterAgentJobRunnerAsync(runnerId, projectId);
        var parentSessionId = $"wt-parent-{Guid.NewGuid():N}";

        var parent = Grains.GetGrain<IAgentSessionGrain>(parentSessionId);
        var parentDefinition = new AgentExecutionDefinition(
            "parent instructions",
            "opencode",
            "gpt-5.6-luna",
            "xhigh",
            [],
            [new AllowedSubagentSnapshot("agent-wt-target", "Target", "target description"),
             new AllowedSubagentSnapshot("agent-inherit-target", "Target", "target description"),
             new AllowedSubagentSnapshot("agent-wt-rej-target", "Target", "target description"),
             new AllowedSubagentSnapshot("agent-wt-unk-target", "Target", "target description")]);
        await parent.OpenAsync(new OpenAgentSessionCommand(
            runnerId,
            "opencode",
            "/workspace/parent",
            Metadata: Metadata(projectId, "agent-parent", "agent-launch"),
            Definition: parentDefinition));
        await parent.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(
            "parent-runtime",
            ExpectedRunnerId: runnerId,
            ExpectedRuntime: "opencode"));
        return (runnerId, projectId, parentSessionId);
    }

    private async Task LaunchAsync(
        string projectId,
        string parentSessionId,
        string childSessionId,
        string idempotencyKey,
        string targetAgentId,
        string prompt,
        string runnerId,
        string workDir,
        WorkspaceMode? workspaceMode,
        WorkspaceRepositorySnapshot? repository)
    {
        var workspaceToken = workspaceMode is WorkspaceMode.Worktree ? "worktree" : "inherit";
        var fingerprint = AgentLaunchCoordinatorCodec.SpawnFingerprint(targetAgentId, prompt, workspaceToken);
        var inputId = $"wt-input-{Guid.NewGuid():N}";
        var turnId = $"wt-turn-{Guid.NewGuid():N}";

        await Grains.GetGrain<ISpawnRequestFenceGrain>(
                AgentLaunchCoordinatorCodec.KeyFor(projectId, parentSessionId, idempotencyKey))
            .StartAsync(new SpawnRequestFence(
                projectId,
                parentSessionId,
                idempotencyKey,
                fingerprint,
                SpawnRequestFenceOutcome.ValidationPending));

        var request = new AgentLaunchCoordinatorRequest(
            prompt,
            targetAgentId,
            "opencode",
            workDir,
            null, null, null, null,
            ExactPromptFingerprint: true);
        var startup = new AgentSessionStartup(
            projectId,
            childSessionId,
            parentSessionId,
            [],
            "mo agent spawn",
            WorkDir: workDir,
            PinnedRunnerId: runnerId,
            AgentId: targetAgentId,
            AgentName: "Target");
        var command = new AgentLaunchCoordinatorCommandEnvelope(
            ProjectId: projectId,
            IdempotencyKey: idempotencyKey,
            AgentId: targetAgentId,
            AgentName: "Target",
            AgentInstructions: "child instructions",
            AgentConfigJson: null,
            Model: "gpt-5.6-luna",
            Variant: "xhigh",
            Runtime: "opencode",
            Prompt: prompt,
            WorkspacePath: workDir,
            IssueNumber: null,
            EpicNumber: null,
            Repository: null,
            Title: null,
            Request: request,
            PreMintedInputId: inputId,
            PreMintedTurnId: turnId,
            PreMintedSessionId: childSessionId,
            AllowedSubagents: [],
            PinnedRunnerId: runnerId,
            AgentSessionStartup: startup,
            ParentSessionId: parentSessionId,
            ParentAgentId: "agent-parent",
            ParentExpectedWorkDir: workDir,
            ParentExpectedRunnerId: runnerId,
            ParentExpectedRuntime: "opencode",
            ParentExpectedRuntimeSessionId: "parent-runtime",
            ParentExpectedBindingEpoch: 1,
            ParentLinkEdgeId: $"edge-{AgentLaunchCoordinatorCodec.StableToken(projectId + parentSessionId + idempotencyKey)}",
            SpawnRequestFingerprint: fingerprint,
            WorkspaceMode: workspaceMode,
            WorkspaceRepository: repository);

        await Grains.GetGrain<IAgentLaunchCoordinatorGrain>(
                AgentLaunchCoordinatorCodec.KeyFor(projectId, parentSessionId, idempotencyKey))
            .LaunchAsync(command);
    }

    private async Task AssertChildWorkDirAsync(string projectId, string childSessionId, string expected)
    {
        var session = await GetSessionAsync(projectId, childSessionId);
        Assert.NotNull(session);
        Assert.Equal(expected, session!.Runtime.WorkDir);
    }

    private async Task<AgentSession?> GetSessionAsync(string projectId, string childSessionId)
    {
        var record = (await SessionsByIdsAsync(projectId, [childSessionId])).FirstOrDefault();
        return record?.Session;
    }

    private async Task<IReadOnlyList<AgentSessionRecord>> SessionsByIdsAsync(string projectId, IReadOnlyList<string> ids)
    {
        await using var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateAsyncScope();
        var sessions = scope.ServiceProvider.GetRequiredService<AgentSessionQuery>();
        return await sessions.ListByIdsAsync(ids);
    }

    private static AgentSessionMetadata Metadata(string projectId, string agentId, string source) =>
        new(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
            [AgentSessionQueryMetadataKeys.SourceKind] = source,
            [GenericAgentSessionMetadata.AgentId] = agentId,
            [GenericAgentSessionMetadata.AgentName] = agentId,
        });
}
