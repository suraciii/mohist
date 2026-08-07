using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.Infrastructure;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Specs.Agent.Api;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent;

/// <summary>
/// Scope-A workspace source coverage that the unit domain test
/// (WorkspaceRepositoryDomainTests) cannot reach: the grain wiring
/// that turns <c>workspace_source_confirmed</c> /
/// <c>workspace_source_rejected</c> control facts into durable
/// <see cref="WorkspaceRepository"/> state transfers without ever
/// persisting them as transcript content, plus the launch-route
/// <c>repository_not_found</c> fail-fast gate that fires before any
/// session or job is created.
/// </summary>
[Collection("MohistIntegration")]
public class AgentWorkspaceSourceSpecs : AgentSessionLaunchRoutesTestSupport
{
    private const string RepoName = "main";
    private const string RepoGitUrl = "https://example.test/mohist.git";
    private const string RepoBaseBranch = "master";

    private static readonly WorkspaceRepositorySnapshot Snapshot =
        new(RepoName, RepoGitUrl, RepoBaseBranch);

    public AgentWorkspaceSourceSpecs(MohistIntegrationFixture fixture) : base(fixture)
    {
    }

    // ---- Route gate: unknown repository fails closed, no session created ----

    [Fact]
    public async Task Launch_WithUnknownRepository_ReturnsRepositoryNotFound_AndCreatesNoSession()
    {
        var projectId = await CreateProjectAsync("ws-source-unknown-repo");
        var agent = await CreateAgentAsync(projectId, "ws-source-agent");

        var sessionsBefore = await CountAgentLaunchSessionsAsync(projectId);
        using var response = await LaunchAsync(projectId, agent.Id, new
        {
            prompt = "work in the repository",
            context = new { repository = "no-such-repository", workspacePath = "/tmp/ws-source" },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("repository_not_found", body.GetProperty("code").GetString());
        // The gate fires before the launch mints any durable session.
        Assert.Equal(sessionsBefore, await CountAgentLaunchSessionsAsync(projectId));
    }

    // ---- Regression: a Project-backed startup initializes an unconfirmed source ----

    [Fact]
    public async Task Open_WithWorkspaceRepositoryStartup_InitializesUnconfirmedSource()
    {
        var grain = await OpenProjectBackedSessionAsync(Snapshot);

        var info = await grain.GetAsync();
        Assert.NotNull(info);
        var source = info!.WorkspaceRepository;
        Assert.NotNull(source);
        Assert.Equal(WorkspaceRepositoryState.Unconfirmed, source!.State);
        Assert.Equal(RepoName, source.Name);
        Assert.Equal(RepoGitUrl, source.GitUrl);
        Assert.Null(source.RejectionReason);
    }

    // ---- Confirm transfers durable state only, never transcript ----

    [Fact]
    public async Task WorkspaceSourceConfirm_TransfersDurableStateOnly_AndNeverEntersTranscript()
    {
        var grain = await OpenProjectBackedSessionAsync(Snapshot);

        // The grain returns the transcript entries it persisted. An empty
        // list means the control fact was consumed as durable state and
        // produced no transcript content — the proof that control events
        // stay out of the transcript.
        var produced = await grain.AppendRuntimeEventsAsync(Command(ConfirmEvent()));

        Assert.Empty(produced);

        var info = await grain.GetAsync();
        Assert.Equal(WorkspaceRepositoryState.Confirmed, info!.WorkspaceRepository!.State);
        Assert.True(info.WorkspaceRepository!.IsConfirmed);
        Assert.Null(info.WorkspaceRepository.RejectionReason);
    }

    [Theory]
    [InlineData("other", RepoGitUrl)]                  // repository name mismatch
    [InlineData(RepoName, "https://example.test/other.git")] // gitUrl mismatch
    public async Task WorkspaceSourceConfirm_NameOrGitUrlMismatch_IsNoOp(string name, string gitUrl)
    {
        var grain = await OpenProjectBackedSessionAsync(Snapshot);

        var produced = await grain.AppendRuntimeEventsAsync(Command(ConfirmEvent(name, gitUrl)));

        Assert.Empty(produced);
        var info = await grain.GetAsync();
        Assert.Equal(WorkspaceRepositoryState.Unconfirmed, info!.WorkspaceRepository!.State);
    }

    [Fact]
    public async Task WorkspaceSourceConfirm_DuplicateReport_IsIdempotent()
    {
        var grain = await OpenProjectBackedSessionAsync(Snapshot);

        var first = await grain.AppendRuntimeEventsAsync(Command(ConfirmEvent()));
        var second = await grain.AppendRuntimeEventsAsync(Command(ConfirmEvent()));

        Assert.Empty(first);
        Assert.Empty(second);
        var info = await grain.GetAsync();
        Assert.Equal(WorkspaceRepositoryState.Confirmed, info!.WorkspaceRepository!.State);
    }

    // ---- Reject transfers durable state only, is terminal ----

    [Fact]
    public async Task WorkspaceSourceReject_TransfersDurableStateOnly_AndIsTerminal()
    {
        var grain = await OpenProjectBackedSessionAsync(Snapshot);

        var produced = await grain.AppendRuntimeEventsAsync(
            Command(RejectEvent("origin-mismatch")));

        Assert.Empty(produced);

        var info = await grain.GetAsync();
        var source = info!.WorkspaceRepository!;
        Assert.Equal(WorkspaceRepositoryState.Rejected, source.State);
        Assert.Equal(WorkspaceSourceRejectionReason.OriginMismatch, source.RejectionReason);

        // Rejected is terminal: a later confirmation report must not revive
        // the unconfirmed source, and must not reach the transcript.
        var revive = await grain.AppendRuntimeEventsAsync(Command(ConfirmEvent()));
        Assert.Empty(revive);
        var afterRevive = await grain.GetAsync();
        Assert.Equal(WorkspaceRepositoryState.Rejected, afterRevive!.WorkspaceRepository!.State);
    }

    [Fact]
    public async Task WorkspaceSourceReject_UnknownReasonFallsBackToNotRunnerOwned()
    {
        var grain = await OpenProjectBackedSessionAsync(Snapshot);

        await grain.AppendRuntimeEventsAsync(Command(RejectEvent("something-unexpected")));

        var info = await grain.GetAsync();
        Assert.Equal(WorkspaceRepositoryState.Rejected, info!.WorkspaceRepository!.State);
        Assert.Equal(WorkspaceSourceRejectionReason.NotRunnerOwned, info.WorkspaceRepository!.RejectionReason);
    }

    // ---- helpers ----

    private async Task<IAgentSessionGrain> OpenProjectBackedSessionAsync(WorkspaceRepositorySnapshot snapshot)
    {
        var sessionId = $"agent-session-ws-source-{Guid.NewGuid():N}";
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        var metadata = new AgentSessionMetadata(
            Labels: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AgentSessionQueryMetadataKeys.ProjectId] = "ws-source-spec-project",
                [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
                ["mohist.io/agent-id"] = "ws-source-spec-agent",
            });
        var startup = new AgentSessionStartup(
            ProjectId: "ws-source-spec-project",
            SessionId: sessionId,
            ParentSessionId: null,
            AllowedSubagents: Array.Empty<AllowedSubagentSnapshot>(),
            SpawnCommand: $"mo agent spawn --parent-session {sessionId}",
            WorkDir: "/work/ws-source-spec",
            PinnedRunnerId: null,
            AgentId: "ws-source-spec-agent",
            AgentName: "ws-source-spec-agent",
            WorkspaceRepository: snapshot);
        await grain.OpenAsync(new OpenAgentSessionCommand(
            RunnerId: "ws-source-runner",
            AgentRuntime: "opencode",
            WorkDir: "/work/ws-source-spec",
            Metadata: metadata,
            AgentSessionStartup: startup));
        return grain;
    }

    private static AppendAgentSessionRuntimeEventsCommand Command(params AgentSessionRuntimeEventInput[] events) =>
        new(events, RuntimeSessionId: "ws-source-reporter");

    private static AgentSessionRuntimeEventInput ConfirmEvent(
        string name = RepoName, string gitUrl = RepoGitUrl) =>
        new("workspace_source_confirmed", PayloadJson(("repositoryName", name), ("gitUrl", gitUrl)));

    private static AgentSessionRuntimeEventInput RejectEvent(string reason,
        string name = RepoName, string gitUrl = RepoGitUrl) =>
        new("workspace_source_rejected",
            PayloadJson(("repositoryName", name), ("gitUrl", gitUrl), ("reason", reason)));

    private static string PayloadJson(params (string Key, string? Value)[] pairs)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in pairs)
            dict[key] = value;
        return JsonSerializer.Serialize(dict);
    }
}
