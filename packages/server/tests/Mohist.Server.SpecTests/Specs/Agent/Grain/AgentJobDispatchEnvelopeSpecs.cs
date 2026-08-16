using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.TestSupport;
using Mohist.Server.SpecTests.Specs.Workflow;
using Orleans;
using Xunit;
namespace Mohist.Server.SpecTests.Specs.Agent.Grain;

[Collection("AgentJobGrain")]
public class AgentJobDispatchEnvelopeSpecs : AgentJobGrainTestSupport
{
    public AgentJobDispatchEnvelopeSpecs(AgentJobGrainFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task SubmitAsync_WithAgentDefinition_EmitsFlatPromptInstructionsModel_OnDispatchEnvelope()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync($"agent-job-agent-source-runner-{Guid.NewGuid():N}");
        var jobKey = $"agent-job-agent-source-{Guid.NewGuid():N}";
        var job = JobGrain(jobKey);
        var sessionId = $"agent-session-{Guid.NewGuid():N}";

        // An explicit reasoningEffort is only claimable when the runner
        // advertises a complete catalog and a ready readiness witness for
        // that runtime (stage-1 capability fence, #557).
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "agent-job-host",
            projectId,
            ConnectionGeneration: "connection-1",
            RuntimeCatalogs: new Dictionary<string, RuntimeCatalogEntry>
            {
                ["pi"] = new(
                    Models: ["openai/gpt-5.5"],
                    Variants: new Dictionary<string, string[]>
                    {
                        ["openai/gpt-5.5"] = ["balanced", "high"],
                    },
                    SupportsReasoningEffort: true,
                    Complete: true,
                    CapabilityRevision: "catalog-rev-envelope-1"),
            }));
        await runner.ObserveRuntimeReadinessAsync(
            "connection-1",
            [new RuntimeReadinessWitness("pi", Ready: true, Generation: 1)]);

        var instructions = "Always respond in formal English; refuse non-code tasks.";
        var configElement = JsonDocument.Parse("{\"type\":\"pi\",\"model\":\"openai/gpt-5.5\",\"reasoningEffort\":\"high\",\"variant\":\"balanced\"}").RootElement.Clone();

        var input = new AgentJobInput(
            Prompt: "summarize the diff",
            Model: "openai/gpt-5.5",
            WorkspacePath: "/tmp/agent-job-agent-source",
            ProjectId: projectId,
            Runtime: "pi",
            AgentId: "agent-7",
            AgentInstructions: instructions,
            AgentConfig: configElement,
            AgentSessionId: sessionId,
            Variant: "balanced",
            ReasoningEffort: "high");

        await job.SubmitAsync(input);
        await WaitForStatusAsync(
            job,
            AgentJobStatus.Running,
            TimeSpan.FromSeconds(5),
            new RunnerPollRequest(
                [],
                [],
                RuntimeReadiness: [new RuntimeReadinessWitness("pi", Ready: true, Generation: 1)],
                ConnectionGeneration: "connection-1"));

        var polled = await Grains.GetGrain<IRunnerGrain>(runnerId).PollAsync(_fixture.Cluster.GetSiloServiceProvider(null));

        Assert.NotNull(polled);
        Assert.Equal(WorkDispatchOwnerKinds.AgentJob, polled!.OwnerKind);
        Assert.Equal(jobKey, polled.AgentJobId);
        Assert.Equal(projectId, polled.ProjectId);
        Assert.Equal(sessionId, polled.AgentSessionId);

        Assert.False(string.IsNullOrWhiteSpace(polled.With));
        var with = JsonSerializer.Deserialize<JsonElement>(polled.With!);

        // New flat Agent-owned payload: prompt / instructions / model are
        // sibling string fields; no `agent-launch` envelope, no `agent`
        // field (design D2, #410 T-001 AC).
        Assert.Equal(JsonValueKind.String, with.GetProperty("prompt").ValueKind);
        Assert.Equal("summarize the diff", with.GetProperty("prompt").GetString());
        Assert.Equal(instructions, with.GetProperty("instructions").GetString());
        Assert.Equal("openai/gpt-5.5", with.GetProperty("model").GetString());
        Assert.Equal("high", with.GetProperty("reasoningEffort").GetString());
        Assert.Equal("balanced", with.GetProperty("variant").GetString());
        Assert.False(with.TryGetProperty("agent", out _));
        Assert.False(with.TryGetProperty("agent-launch", out _));
    }

    [Fact]
    public async Task SubmitAsync_WithAgentDefinition_CarriesNoWorkflowActionUses_OnDispatchEnvelope()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync($"agent-job-no-action-uses-runner-{Guid.NewGuid():N}");
        var jobKey = $"agent-job-no-action-uses-{Guid.NewGuid():N}";
        var job = JobGrain(jobKey);

        await job.SubmitAsync(MakeInput("raw prompt", projectId, "/tmp/agent-job-no-action-uses"));
        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));

        var polled = await Grains.GetGrain<IRunnerGrain>(runnerId).PollAsync(_fixture.Cluster.GetSiloServiceProvider(null));

        Assert.NotNull(polled);
        Assert.Null(polled!.Uses);
    }

    [Fact]
    public async Task SubmitAsync_RawPromptOnly_PassesBarePromptToDispatchEnvelope_AndLeavesNewFieldsUnset()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync($"agent-job-raw-only-runner-{Guid.NewGuid():N}");
        var jobKey = $"agent-job-raw-only-{Guid.NewGuid():N}";
        var job = JobGrain(jobKey);

        await job.SubmitAsync(MakeInput("raw prompt only", projectId, "/tmp/agent-job-raw-only"));
        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));

        var polled = await Grains.GetGrain<IRunnerGrain>(runnerId).PollAsync(_fixture.Cluster.GetSiloServiceProvider(null));

        Assert.NotNull(polled);
        Assert.Equal(WorkDispatchOwnerKinds.AgentJob, polled!.OwnerKind);
        Assert.Equal(jobKey, polled.AgentJobId);
        Assert.Equal(projectId, polled.ProjectId);
        Assert.Null(polled.AgentSessionId);

        var with = JsonSerializer.Deserialize<JsonElement>(polled.With!);
        Assert.Equal(JsonValueKind.String, with.GetProperty("prompt").ValueKind);
        Assert.Equal("raw prompt only", with.GetProperty("prompt").GetString());

        var variables = JsonSerializer.Deserialize<JsonElement>(polled.Variables!);
        Assert.Equal("/tmp/agent-job-raw-only", variables.GetProperty("workspace").GetProperty("path").GetString());
    }

    [Fact]
    public async Task SubmitAsync_AgentJobWithAgentSessionId_PopulatesSessionIdOnDispatchEnvelope()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync($"agent-job-session-runner-{Guid.NewGuid():N}");
        var jobKey = $"agent-job-session-{Guid.NewGuid():N}";
        var job = JobGrain(jobKey);
        var sessionId = $"generic-session-{Guid.NewGuid():N}";

        var input = new AgentJobInput(
            Prompt: "ask the agent",
            WorkspacePath: "/tmp/agent-job-session",
            ProjectId: projectId,
            AgentId: "agent-42",
            AgentInstructions: "be brief",
            AgentSessionId: sessionId);

        await job.SubmitAsync(input);
        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));

        var polled = await Grains.GetGrain<IRunnerGrain>(runnerId).PollAsync(_fixture.Cluster.GetSiloServiceProvider(null));

        Assert.NotNull(polled);
        Assert.Equal(projectId, polled!.ProjectId);
        Assert.Equal(sessionId, polled.AgentSessionId);
    }

    [Fact]
    public async Task SubmitAsync_AgentJobWithoutSessionId_LeavesAgentSessionIdUnset()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync($"agent-job-no-session-runner-{Guid.NewGuid():N}");
        var jobKey = $"agent-job-no-session-{Guid.NewGuid():N}";
        var job = JobGrain(jobKey);

        var input = new AgentJobInput(
            Prompt: "no session",
            WorkspacePath: "/tmp/agent-job-no-session",
            ProjectId: projectId,
            AgentId: "agent-test");

        await job.SubmitAsync(input);
        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));

        var polled = await Grains.GetGrain<IRunnerGrain>(runnerId).PollAsync(_fixture.Cluster.GetSiloServiceProvider(null));

        Assert.NotNull(polled);
        Assert.Equal(projectId, polled!.ProjectId);
        Assert.Null(polled.AgentSessionId);
    }

    [Fact]
    public async Task SubmitAsync_PolledDispatch_ExposesProjectIdAndAgentSessionIdThroughHttpPoll()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync($"agent-job-http-project-runner-{Guid.NewGuid():N}");
        var jobKey = $"agent-job-http-project-{Guid.NewGuid():N}";
        var job = JobGrain(jobKey);
        var sessionId = $"http-session-{Guid.NewGuid():N}";

        var input = new AgentJobInput(
            Prompt: "expose via http",
            WorkspacePath: "/tmp/agent-job-http-project",
            ProjectId: projectId,
            AgentId: "agent-http",
            AgentInstructions: "concise",
            AgentSessionId: sessionId);

        await job.SubmitAsync(input);
        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));

        var polled = await Grains.GetGrain<IRunnerGrain>(runnerId).PollAsync(_fixture.Cluster.GetSiloServiceProvider(null));

        Assert.NotNull(polled);
        Assert.Equal(projectId, polled!.ProjectId);
        Assert.Equal(sessionId, polled.AgentSessionId);
    }

    [Fact]
    public async Task SubmitAsync_WithResolvedRuntime_EmitsRuntimeOnDispatchEnvelope()
    {
        // Issue-452 design D4: the dispatch envelope carries the
        // snapshot-fixed runtime so the runner executor can pick the
        // right runtime. Both opencode and pi are accepted values.
        await AssertDispatchEnvelopeEmitsRuntimeAsync("opencode");
        await AssertDispatchEnvelopeEmitsRuntimeAsync("pi");
    }

    private async Task AssertDispatchEnvelopeEmitsRuntimeAsync(string runtime)
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync(
            $"agent-job-runtime-dispatch-{runtime}-{Guid.NewGuid():N}");
        var jobKey = $"agent-job-runtime-dispatch-{runtime}-{Guid.NewGuid():N}";
        var job = JobGrain(jobKey);

        var input = new AgentJobInput(
            Prompt: $"run on {runtime}",
            WorkspacePath: $"/tmp/agent-job-runtime-{runtime}",
            ProjectId: projectId,
            AgentId: "agent-test",
            Runtime: runtime);

        await job.SubmitAsync(input);
        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));

        var polled = await Grains.GetGrain<IRunnerGrain>(runnerId).PollAsync(_fixture.Cluster.GetSiloServiceProvider(null));
        Assert.NotNull(polled);
        Assert.False(string.IsNullOrWhiteSpace(polled!.With));
        var with = JsonSerializer.Deserialize<JsonElement>(polled.With!);
        Assert.Equal(runtime, with.GetProperty("runtime").GetString());
    }

    [Fact]
    public async Task SubmitAsync_WithReasoningEffort_DeliversEffortOnDispatchEnvelopeBesideModelAndVariant()
    {
        // Issue-557 T-002: the frozen execution tuple's canonical effort
        // member is written into the dispatch `with` payload exactly when
        // non-empty, beside model and variant, so the runner applies the
        // launch-time effort without re-reading the Agent definition.
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync(
            $"agent-job-effort-dispatch-runner-{Guid.NewGuid():N}");
        var jobKey = $"agent-job-effort-dispatch-{Guid.NewGuid():N}";
        var job = JobGrain(jobKey);

        var input = new AgentJobInput(
            Prompt: "summarize with effort",
            Model: "openai/gpt-5.5",
            WorkspacePath: "/tmp/agent-job-effort-dispatch",
            ProjectId: projectId,
            AgentId: "agent-effort",
            AgentInstructions: "be terse",
            Variant: "balanced",
            Runtime: "opencode",
            ReasoningEffort: "high");

        await job.SubmitAsync(input);
        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));

        var polled = await Grains.GetGrain<IRunnerGrain>(runnerId).PollAsync(_fixture.Cluster.GetSiloServiceProvider(null));
        Assert.NotNull(polled);

        Assert.False(string.IsNullOrWhiteSpace(polled!.With));
        var with = JsonSerializer.Deserialize<JsonElement>(polled.With!);
        Assert.Equal("openai/gpt-5.5", with.GetProperty("model").GetString());
        Assert.Equal("balanced", with.GetProperty("variant").GetString());
        Assert.Equal("high", with.GetProperty("reasoningEffort").GetString());

        // The AgentDefinition snapshot on the dispatch carries the same
        // frozen tuple member.
        Assert.NotNull(polled.AgentDefinition);
        Assert.Equal("high", polled.AgentDefinition!.ReasoningEffort);
        Assert.Equal("balanced", polled.AgentDefinition.Variant);
    }

    [Fact]
    public async Task SubmitAsync_WithoutReasoningEffort_OmitsKeyAndFreezesNullOnDefinition()
    {
        // Absent effort is written as absent — no `reasoningEffort` key in
        // the `with` payload, no synthesized default on the definition.
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync(
            $"agent-job-no-effort-dispatch-runner-{Guid.NewGuid():N}");
        var jobKey = $"agent-job-no-effort-dispatch-{Guid.NewGuid():N}";
        var job = JobGrain(jobKey);

        var input = new AgentJobInput(
            Prompt: "summarize without effort",
            Model: "openai/gpt-5.5",
            WorkspacePath: "/tmp/agent-job-no-effort-dispatch",
            ProjectId: projectId,
            AgentId: "agent-no-effort",
            Variant: "balanced");

        await job.SubmitAsync(input);
        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));

        var polled = await Grains.GetGrain<IRunnerGrain>(runnerId).PollAsync(_fixture.Cluster.GetSiloServiceProvider(null));
        Assert.NotNull(polled);

        var with = JsonSerializer.Deserialize<JsonElement>(polled!.With!);
        Assert.False(with.TryGetProperty("reasoningEffort", out _),
            "an absent effort must not be written into the dispatch payload");
        Assert.NotNull(polled.AgentDefinition);
        Assert.Null(polled.AgentDefinition!.ReasoningEffort);
    }
}
