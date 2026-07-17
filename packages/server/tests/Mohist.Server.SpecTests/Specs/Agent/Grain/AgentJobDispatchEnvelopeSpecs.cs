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
using Mohist.Server.SpecTests.Support;
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
    public async Task SubmitAsync_WithAgentDefinition_ComposesInstructionsConfigAndPrompt_OnDispatchEnvelope()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync($"agent-job-agent-source-runner-{Guid.NewGuid():N}");
        var jobKey = $"agent-job-agent-source-{Guid.NewGuid():N}";
        var job = JobGrain(jobKey);
        var sessionId = $"agent-session-{Guid.NewGuid():N}";

        var instructions = "Always respond in formal English; refuse non-code tasks.";
        var configElement = JsonDocument.Parse("{\"type\":\"opencode\",\"model\":\"openai/gpt-5.5\"}").RootElement.Clone();

        var input = new AgentJobInput(
            Prompt: "summarize the diff",
            Model: "openai/gpt-5.5",
            WorkspacePath: "/tmp/agent-job-agent-source",
            ProjectId: projectId,
            AgentId: "agent-7",
            AgentInstructions: instructions,
            AgentConfig: configElement,
            AgentSessionId: sessionId);

        await job.SubmitAsync(input);
        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));

        var polled = await Grains.GetGrain<IRunnerGrain>(runnerId).PollAsync(_fixture.Cluster.GetSiloServiceProvider(null));

        Assert.NotNull(polled);
        Assert.Equal(WorkDispatchOwnerKinds.AgentJob, polled!.OwnerKind);
        Assert.Equal(jobKey, polled.AgentJobId);
        Assert.Equal(projectId, polled.ProjectId);
        Assert.Equal(sessionId, polled.AgentSessionId);

        Assert.False(string.IsNullOrWhiteSpace(polled.With));
        var with = JsonSerializer.Deserialize<JsonElement>(polled.With!);
        var promptValue = with.GetProperty("prompt");
        Assert.Equal(JsonValueKind.Object, promptValue.ValueKind);

        var agentLaunch = promptValue.GetProperty("agent-launch");
        Assert.Equal(instructions, agentLaunch.GetProperty("instructions").GetString());
        Assert.Equal("openai/gpt-5.5", agentLaunch.GetProperty("config").GetProperty("model").GetString());
        Assert.Equal("summarize the diff", agentLaunch.GetProperty("prompt").GetString());

        Assert.Equal("openai/gpt-5.5", with.GetProperty("model").GetString());
        Assert.Equal("openai/gpt-5.5", with.GetProperty("agent").GetProperty("model").GetString());
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
            ProjectId: projectId);

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
}
