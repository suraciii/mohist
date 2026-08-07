using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Contracts;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Grain;

/// <summary>
/// End-to-end coverage for the agent-startup-context launch channel
/// (issue 516, capability <c>agent-startup-context</c>). Verifies
/// that <see cref="AgentStartupContext"/> threads through the launch
/// chain to dispatch (composition), is excluded from the launch
/// fingerprint (background is volatile), is persisted on the
/// SessionInput audit record (so a later observer can inspect the
/// attestation), and does not leak into follow-up inputs.
/// </summary>
[Collection("AgentJobGrain")]
public sealed class AgentStartupContextLaunchSpecs : AgentJobGrainTestSupport
{
    public AgentStartupContextLaunchSpecs(AgentJobGrainFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task Launch_WithStartupContext_ComposesReadOnlyBackground_OnDispatchPrompt()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync(
            $"agent-startup-context-composition-{Guid.NewGuid():N}");
        var jobKey = $"agent-job-startup-context-composition-{Guid.NewGuid():N}";
        var sessionId = $"agent-session-startup-context-composition-{Guid.NewGuid():N}";
        var session = Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.OpenAsync(new OpenAgentSessionCommand(
            RunnerId: string.Empty,
            AgentRuntime: "opencode",
            WorkDir: "/tmp/agent-job-startup-context",
            Metadata: BuildSessionMetadata(projectId)));

        var startupContext = new AgentStartupContext(
            Text: "alice: should we ship?\nbob: yes",
            Provenance: new AgentStartupContextProvenance(
                Source: "slack-thread-history",
                Truncated: false,
                TruncationMarker: null,
                OmittedOldestMessageCount: 0));

        var input = new AgentJobInput(
            Prompt: "review the change",
            WorkspacePath: "/tmp/agent-job-startup-context",
            ProjectId: projectId,
            Runtime: "opencode",
            AgentId: "agent-startup-context",
            AgentInstructions: "be brief",
            AgentSessionId: sessionId,
            Variant: null,
            Skills: null,
            InitialInputId: $"input-{Guid.NewGuid():N}",
            InitialTurnId: $"turn-{Guid.NewGuid():N}",
            Attachments: null,
            StartupContext: startupContext);

        var job = JobGrain(jobKey);
        await job.SubmitAsync(input);
        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));

        var polled = await Grains.GetGrain<IRunnerGrain>(runnerId).PollAsync(
            _fixture.Cluster.GetSiloServiceProvider(null));
        Assert.NotNull(polled);
        var with = JsonSerializer.Deserialize<JsonElement>(polled!.With!);

        var prompt = with.GetProperty("prompt").GetString();
        Assert.NotNull(prompt);
        Assert.StartsWith(AgentStartupContextComposer.BackgroundHeader, prompt!, StringComparison.Ordinal);
        Assert.Contains("alice: should we ship?\nbob: yes", prompt!, StringComparison.Ordinal);
        Assert.EndsWith("review the change", prompt, StringComparison.Ordinal);

        // Instructions, Runtime, Model, Variant, Skills must be
        // unchanged with vs without startup context — the
        // background is untrusted user input, never instructions.
        Assert.Equal("be brief", with.GetProperty("instructions").GetString());
        Assert.Equal("opencode", with.GetProperty("runtime").GetString());
    }

    [Fact]
    public async Task Launch_WithoutStartupContext_EmitsBareTaskPrompt_OnDispatch()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync(
            $"agent-startup-context-none-{Guid.NewGuid():N}");
        var jobKey = $"agent-job-startup-context-none-{Guid.NewGuid():N}";
        var sessionId = $"agent-session-startup-none-{Guid.NewGuid():N}";
        await Grains.GetGrain<IAgentSessionGrain>(sessionId).OpenAsync(new OpenAgentSessionCommand(
            RunnerId: string.Empty,
            AgentRuntime: "opencode",
            WorkDir: "/tmp/agent-job-startup-none",
            Metadata: BuildSessionMetadata(projectId)));

        var input = new AgentJobInput(
            Prompt: "no background",
            WorkspacePath: "/tmp/agent-job-startup-none",
            ProjectId: projectId,
            AgentId: "agent-no-startup",
            AgentInstructions: "be brief",
            AgentSessionId: sessionId,
            InitialInputId: $"input-{Guid.NewGuid():N}",
            InitialTurnId: $"turn-{Guid.NewGuid():N}");

        var job = JobGrain(jobKey);
        await job.SubmitAsync(input);
        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));

        var polled = await Grains.GetGrain<IRunnerGrain>(runnerId).PollAsync(
            _fixture.Cluster.GetSiloServiceProvider(null));
        Assert.NotNull(polled);
        var with = JsonSerializer.Deserialize<JsonElement>(polled!.With!);
        Assert.Equal("no background", with.GetProperty("prompt").GetString());
    }

    [Fact]
    public async Task Launch_WithTruncatedStartupContext_AttestationMarker_ReachesDispatchPromptAndAudit()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync(
            $"agent-startup-context-truncated-{Guid.NewGuid():N}");
        var jobKey = $"agent-job-startup-context-truncated-{Guid.NewGuid():N}";
        var sessionId = $"agent-session-truncated-{Guid.NewGuid():N}";
        var inputId = $"input-{Guid.NewGuid():N}";
        var turnId = $"turn-{Guid.NewGuid():N}";
        await Grains.GetGrain<IAgentSessionGrain>(sessionId).OpenAsync(new OpenAgentSessionCommand(
            RunnerId: string.Empty,
            AgentRuntime: "opencode",
            WorkDir: "/tmp/agent-job-startup-truncated",
            Metadata: BuildSessionMetadata(projectId)));

        var startupContext = new AgentStartupContext(
            Text: "newest discussion message",
            Provenance: new AgentStartupContextProvenance(
                Source: "slack-thread-history",
                Truncated: true,
                TruncationMarker: "10 oldest messages omitted",
                OmittedOldestMessageCount: 10));

        var input = new AgentJobInput(
            Prompt: "summarize",
            WorkspacePath: "/tmp/agent-job-startup-truncated",
            ProjectId: projectId,
            AgentId: "agent-truncated",
            AgentSessionId: sessionId,
            InitialInputId: inputId,
            InitialTurnId: turnId,
            StartupContext: startupContext);

        var job = JobGrain(jobKey);
        await job.SubmitAsync(input);
        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));

        var polled = await Grains.GetGrain<IRunnerGrain>(runnerId).PollAsync(
            _fixture.Cluster.GetSiloServiceProvider(null));
        Assert.NotNull(polled);
        var prompt = JsonSerializer.Deserialize<JsonElement>(polled!.With!).GetProperty("prompt").GetString();
        Assert.NotNull(prompt);
        Assert.Contains("10 oldest messages omitted", prompt!, StringComparison.Ordinal);
        Assert.Contains("newest discussion message", prompt!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Launch_WithoutStartupContext_DispatchEnvelope_LeavesInstructionsRuntimeModelVariantSkillsUnchanged()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync(
            $"agent-startup-context-capabilities-none-{Guid.NewGuid():N}");
        await AssertDispatchShapeAsync(runnerId, projectId, startupContext: null);
    }

    [Fact]
    public async Task Launch_WithStartupContext_DispatchEnvelope_LeavesInstructionsRuntimeModelVariantSkillsUnchanged()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync(
            $"agent-startup-context-capabilities-set-{Guid.NewGuid():N}");
        await AssertDispatchShapeAsync(runnerId, projectId, startupContext: BuildContext());
    }

    private async Task AssertDispatchShapeAsync(
        string runnerId,
        string projectId,
        AgentStartupContext? startupContext)
    {
        var jobKey = $"agent-job-startup-context-cap-{Guid.NewGuid():N}";
        var sessionId = $"agent-session-startup-cap-{Guid.NewGuid():N}";
        await Grains.GetGrain<IAgentSessionGrain>(sessionId).OpenAsync(new OpenAgentSessionCommand(
            RunnerId: string.Empty,
            AgentRuntime: "opencode",
            WorkDir: "/tmp/agent-job-startup-cap",
            Metadata: BuildSessionMetadata(projectId)));

        var input = new AgentJobInput(
            Prompt: "review the change",
            WorkspacePath: "/tmp/agent-job-startup-cap",
            ProjectId: projectId,
            Runtime: "opencode",
            AgentId: "agent-cap",
            AgentInstructions: "be brief and structured",
            Model: "openai/gpt-5.5",
            AgentSessionId: sessionId,
            Variant: "high",
            Skills: new[] { "coding", "research" },
            InitialInputId: $"input-{Guid.NewGuid():N}",
            InitialTurnId: $"turn-{Guid.NewGuid():N}",
            StartupContext: startupContext);

        var job = JobGrain(jobKey);
        await job.SubmitAsync(input);
        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));

        var polled = await Grains.GetGrain<IRunnerGrain>(runnerId).PollAsync(
            _fixture.Cluster.GetSiloServiceProvider(null));
        Assert.NotNull(polled);
        var with = JsonSerializer.Deserialize<JsonElement>(polled!.With!);

        Assert.Equal("be brief and structured", with.GetProperty("instructions").GetString());
        Assert.Equal("opencode", with.GetProperty("runtime").GetString());
        Assert.Equal("openai/gpt-5.5", with.GetProperty("model").GetString());
        Assert.Equal("high", with.GetProperty("variant").GetString());
        var skills = with.GetProperty("skills").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { "coding", "research" }, skills);

        var prompt = with.GetProperty("prompt").GetString();
        Assert.NotNull(prompt);
        if (startupContext is null)
        {
            Assert.Equal("review the change", prompt);
        }
        else
        {
            Assert.StartsWith(AgentStartupContextComposer.BackgroundHeader, prompt!, StringComparison.Ordinal);
            Assert.EndsWith("review the change", prompt, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task LaunchCoordinator_WithVaryingStartupContext_ProducesIdenticalFingerprint()
    {
        var projectId = $"startup-context-fingerprint-{Guid.NewGuid():N}";
        var agentId = "agent-fingerprint";
        var prompt = "summarize the diff";
        var shortContext = BuildContext(body: "short");
        var longContext = BuildContext(body: BuildLongBody(), truncated: true);

        var baseRequest = new AgentLaunchCoordinatorRequest(
            prompt, agentId, null, null, null, null, null, null);
        var withShort = new AgentLaunchCoordinatorRequest(
            prompt, agentId, null, null, null, null, null, null,
            StartupContext: shortContext);
        var withLong = new AgentLaunchCoordinatorRequest(
            prompt, agentId, null, null, null, null, null, null,
            StartupContext: longContext);

        var origin = new ConnectionLaunchOrigin("connection", "T1", "U1", "D1", "1.0");

        var fingerprintBase = AgentLaunchCoordinatorCodec.Fingerprint(baseRequest, origin);
        var fingerprintShort = AgentLaunchCoordinatorCodec.Fingerprint(withShort, origin);
        var fingerprintLong = AgentLaunchCoordinatorCodec.Fingerprint(withLong, origin);

        Assert.Equal(fingerprintBase, fingerprintShort);
        Assert.Equal(fingerprintBase, fingerprintLong);
    }

    [Fact]
    public async Task LaunchCoordinator_PlanPersistsStartupContextSnapshot()
    {
        var projectId = $"startup-context-persist-{Guid.NewGuid():N}";
        var agentId = "agent-persist";
        var key = AgentLaunchCoordinatorCodec.KeyFor(projectId, Guid.NewGuid().ToString("N"));
        var startupContext = BuildContext(body: BuildLongBody(), truncated: true);

        var coordinator = Grains.GetGrain<IAgentLaunchCoordinatorGrain>(key);
        var outcome = await coordinator.LaunchAsync(new AgentLaunchCoordinatorCommandEnvelope(
            ProjectId: projectId,
            IdempotencyKey: key.Split('/').Last(),
            AgentId: agentId,
            AgentName: "Persist Agent",
            AgentInstructions: "be brief",
            AgentConfigJson: null,
            Model: null,
            Variant: null,
            Runtime: "opencode",
            Prompt: "summarize",
            WorkspaceName: null,
            WorkspacePath: null,
            IssueNumber: null,
            EpicNumber: null,
            Repository: null,
            Title: null,
            Request: new AgentLaunchCoordinatorRequest(
                "summarize", agentId, null, null, null, null, null, null,
                StartupContext: startupContext),
            ConnectionOrigin: null,
            PreMintedSessionId: $"agent-session-persist-{Guid.NewGuid():N}",
            PreMintedInputId: $"input-{Guid.NewGuid():N}",
            PreMintedTurnId: $"turn-{Guid.NewGuid():N}",
            Attachments: null,
            StartupContext: startupContext));

        var replay = await coordinator.ResumeAsync(new AgentLaunchCoordinatorRequest(
            "summarize", agentId, null, null, null, null, null, null,
            StartupContext: BuildContext(body: "different content")));

        Assert.NotNull(replay);
        Assert.Equal(outcome.SessionId, replay!.SessionId);
        Assert.Equal(outcome.JobKey, replay.JobKey);
        Assert.Equal(outcome.InputId, replay.InputId);
        Assert.Equal(outcome.TurnId, replay.TurnId);
        Assert.True(replay.AlreadyPersisted);
    }

    private static AgentStartupContext BuildContext(
        string body = "discussion body",
        bool truncated = false,
        string? marker = null) =>
        new(
            Text: body,
            Provenance: new AgentStartupContextProvenance(
                Source: "slack-thread-history",
                Truncated: truncated,
                TruncationMarker: marker,
                OmittedOldestMessageCount: truncated ? 10 : 0));

    private static AgentSessionMetadata BuildSessionMetadata(string projectId) =>
        new(
            Labels: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
                [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
                [GenericAgentSessionMetadata.AgentId] = "agent-startup-context",
                [GenericAgentSessionMetadata.AgentName] = "agent-startup-context",
            });

    private static string BuildLongBody()
    {
        var line = "this is one of many older messages in the thread that the caller handed to the agent as background. ";
        var builder = new System.Text.StringBuilder();
        for (var i = 0; i < 200; i++)
        {
            builder.Append(line);
            builder.Append(' ');
        }
        return builder.ToString();
    }
}