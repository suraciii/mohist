using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Orleans;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Services;

/// <summary>
/// Issue-557 T-002: every launch path freezes the canonical
/// <c>reasoningEffort</c> onto the durable execution snapshot beside model
/// and variant, and a frozen snapshot is immutable under later Agent edits
/// or Agent deletion. These specs exercise the composition end-to-end
/// through the real launcher (manual HTTP route and the
/// trigger-labels/mention-shaped <see cref="IAgentLauncher.LaunchAsync"/>
/// pipeline): the AgentSession definition and the AgentJob's
/// <see cref="AgentJobRuntimeSnapshot.ExecutionDefinition"/> must keep the
/// launch-time effort after the Agent is edited or deleted.
/// </summary>
[Collection("RunnerMutationIntegration")]
public class AgentEffortSnapshotFreezeSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public AgentEffortSnapshotFreezeSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ManualHttpLaunch_FreezesEffort_AgentEditToLowDoesNotChangeInFlightJob()
    {
        var projectId = await CreateProjectAsync("effort-freeze-manual");
        var agent = await CreateAgentWithEffortAsync(projectId, "manual-effort-agent");
        var runnerId = $"effort-freeze-manual-runner-{Guid.NewGuid():N}";
        try
        {
            await RegisterRunnerAsync(runnerId, projectId);

            using var launch = await _fixture.Client.LaunchAgentSessionAsync(projectId, agent.Id, new
            {
                prompt = "review with effort",
            });
            Assert.Equal(HttpStatusCode.Created, launch.StatusCode);
            var payload = await launch.Content.ReadFromJsonAsync<JsonElement>();
            var jobId = payload.GetProperty("data").GetProperty("jobId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(jobId));

            // The frozen tuple carried the launch-time effort.
            var job = _fixture.Grains.GetGrain<IAgentJobGrain>(jobId!);
            var frozen = await job.GetRuntimeSnapshotAsync();
            Assert.Equal("high", frozen.ExecutionDefinition?.ReasoningEffort);
            Assert.Equal("balanced", frozen.ExecutionDefinition?.Variant);
            Assert.Equal("openai/gpt-5.6", frozen.ExecutionDefinition?.Model);

            // Editing the Agent's effort to `low` after the job was prepared
            // must not rewrite the in-flight job's frozen `high`.
            using var edit = await _fixture.Client.PatchAsJsonAsync(
                $"/api/projects/{projectId}/agents/{agent.Id}",
                new
                {
                    agentConfig = new
                    {
                        model = "openai/gpt-5.6",
                        variant = "balanced",
                        reasoningEffort = "low",
                    },
                });
            edit.EnsureSuccessStatusCode();

            var afterEdit = await job.GetRuntimeSnapshotAsync();
            Assert.Equal("high", afterEdit.ExecutionDefinition?.ReasoningEffort);
        }
        finally
        {
            // The shared cluster's runner election ignores project scope; a
            // runner left Online here would steal admission for later
            // specs' jobs (they poll their own runner and never see the
            // claim). Mirror AgentLauncherSpecs and unregister on exit.
            await _fixture.Grains
                .GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global)
                .UnregisterAsync(runnerId);
        }
    }

    [Fact]
    public async Task TriggerLabelLaunch_FreezesEffort_AgentDeletionDoesNotChangeInFlightJob()
    {
        var projectId = await CreateProjectAsync("effort-freeze-trigger");
        var agent = await CreateAgentWithEffortAsync(projectId, "trigger-effort-agent");

        AgentLaunchResult result;
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var launcher = scope.ServiceProvider.GetRequiredService<IAgentLauncher>();
            result = await launcher.LaunchAsync(
                agent,
                prompt: "please review",
                new AgentLaunchContext(ProjectId: projectId, WorkspaceName: null),
                triggerLabels: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [GenericAgentSessionMetadata.TriggerEventId] = $"evt_effort_{Guid.NewGuid():N}",
                    [GenericAgentSessionMetadata.TriggerRuleId] = $"sub_effort_{Guid.NewGuid():N}",
                });
        }

        // The AgentSession's durable definition (the follow-up target
        // source) froze the effort beside model and variant.
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var query = scope.ServiceProvider.GetRequiredService<AgentSessionQuery>();
            var records = await query.ListByIdsAsync([result.SessionId]);
            var record = Assert.Single(records);
            Assert.NotNull(record.Session.Settings.Definition);
            Assert.Equal("high", record.Session.Settings.Definition!.ReasoningEffort);
            Assert.Equal("balanced", record.Session.Settings.Definition.Variant);
        }

        var job = _fixture.Grains.GetGrain<IAgentJobGrain>(result.JobKey);
        var frozen = await job.GetRuntimeSnapshotAsync();
        Assert.Equal("high", frozen.ExecutionDefinition?.ReasoningEffort);

        // Deleting the Agent after the job was prepared must not change
        // the in-flight job's frozen effort.
        using var delete = await _fixture.Client.DeleteAsync(
            $"/api/projects/{projectId}/agents/{agent.Id}");
        delete.EnsureSuccessStatusCode();

        var afterDelete = await job.GetRuntimeSnapshotAsync();
        Assert.Equal("high", afterDelete.ExecutionDefinition?.ReasoningEffort);
    }

    private async Task<AgentInfo> CreateAgentWithEffortAsync(string projectId, string name)
    {
        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/agents",
            new
            {
                name,
                description = $"description for {name}",
                instructions = $"instructions for {name}",
                agentConfig = new
                {
                    model = "openai/gpt-5.6",
                    variant = "balanced",
                    reasoningEffort = "high",
                },
                skills = new[] { "coding" },
                maxConcurrentRuns = 1,
            });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var agentId = body.GetProperty("data").GetProperty("id").GetString()!;

        await using var scope = _fixture.Services.CreateAsyncScope();
        var querier = scope.ServiceProvider.GetRequiredService<AgentQuerier>();
        var agent = await querier.GetByIdAsync(projectId, agentId);
        Assert.NotNull(agent);
        return agent!;
    }

    private async Task RegisterRunnerAsync(string runnerId, string projectId)
    {
        var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            $"{runnerId}-host",
            projectId));
        await runner.UpdateAsync(2);
    }

    private async Task<string> CreateProjectAsync(string prefix)
    {
        var raw = $"{prefix}-{Guid.NewGuid():N}".ToLowerInvariant();
        var name = raw.Length > 63 ? raw[..63] : raw;
        using var response = await _fixture.Client.PostAsJsonAsync("/api/projects", new
        {
            name,
            repository = new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main" },
        });
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"CreateProject '{name}' failed: {(int)response.StatusCode} {body}");
        }
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        return payload.GetProperty("data").GetProperty("id").GetString()!;
    }
}
