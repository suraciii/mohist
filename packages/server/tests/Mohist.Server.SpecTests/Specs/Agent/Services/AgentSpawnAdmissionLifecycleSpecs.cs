using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Services;

[Collection("RunnerMutationIntegration")]
public sealed partial class AgentSpawnAdmissionLifecycleSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public AgentSpawnAdmissionLifecycleSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task PublishedNonterminalStop_KeepsFrozenParentValidationPending_AndAllowsOutsideParentEndToEnd()
    {
        var projectId = await CreateProjectAsync("spawn-admission-stop-scope");
        var target = await CreateAgentAsync(projectId, "spawn-admission-target");
        await SeedCompletedTargetExecutionAsync(projectId, target);
        var runnerId = $"spawn-admission-runner-{Guid.NewGuid():N}";
        await _fixture.Grains.GetGrain<IRunnerGrain>(runnerId).RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "spawn-admission-host",
            projectId));

        try
        {
            var insideId = $"spawn-admission-inside-{Guid.NewGuid():N}";
            var outsideId = $"spawn-admission-outside-{Guid.NewGuid():N}";
            await OpenParentAsync(projectId, insideId, runnerId, target);
            await OpenParentAsync(projectId, outsideId, runnerId, target);

            var fence = _fixture.Grains.GetGrain<ISessionTreeMutationFenceGrain>(projectId);
            var stop = await fence.BeginStopSnapshotAsync(new BeginSessionTreeStopSnapshotCommand(
                projectId,
                insideId,
                $"stop-operation-{Guid.NewGuid():N}",
                $"stop-input-{Guid.NewGuid():N}",
                "stop-fingerprint"));
            Assert.Equal(SessionTreeStopSnapshotDisposition.Started, stop.Disposition);
            Assert.True((await fence.GetAsync()).ActiveTreeStop);

            var insideKey = $"inside-key-{Guid.NewGuid():N}";
            AgentSpawnValidationPendingException pending;
            await using (var scope = _fixture.Services.CreateAsyncScope())
            {
                var launcher = scope.ServiceProvider.GetRequiredService<IAgentLauncher>();
                pending = await Assert.ThrowsAsync<AgentSpawnValidationPendingException>(() =>
                    launcher.LaunchSubagentAsync(
                        projectId,
                        insideId,
                        target.Id,
                        "inside frozen stop",
                        insideKey));
            }
            Assert.Equal("parent_tree_stop_in_progress", pending.Reason);
            await AssertNoSpawnArtifactsAsync(
                projectId,
                insideId,
                insideKey,
                target.Id,
                "inside frozen stop");

            AgentLaunchResult outside;
            await using (var scope = _fixture.Services.CreateAsyncScope())
            {
                var launcher = scope.ServiceProvider.GetRequiredService<IAgentLauncher>();
                outside = await launcher.LaunchSubagentAsync(
                    projectId,
                    outsideId,
                    target.Id,
                    "outside frozen stop",
                    $"outside-key-{Guid.NewGuid():N}");
            }

            Assert.Equal(target.Id, outside.AgentId);
            var state = await fence.GetAsync();
            Assert.Equal(
                LinkReservationState.Attached,
                state.Reservations!.Single(item => item.ChildSessionId == outside.SessionId).State);
            Assert.DoesNotContain(
                state.Reservations!,
                item => item.ParentSessionId == insideId && item.State == LinkReservationState.Attached);
        }
        finally
        {
            await _fixture.Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global)
                .UnregisterAsync(runnerId);
        }
    }

    private async Task AssertNoSpawnArtifactsAsync(
        string projectId,
        string parentSessionId,
        string idempotencyKey,
        string targetAgentRef,
        string prompt)
    {
        var coordinatorKey = AgentLaunchCoordinatorCodec.KeyFor(projectId, parentSessionId, idempotencyKey);
        var requestFence = await _fixture.Grains.GetGrain<ISpawnRequestFenceGrain>(coordinatorKey).GetAsync();
        Assert.NotNull(requestFence);
        Assert.Equal(SpawnRequestFenceOutcome.ValidationPending, requestFence!.Outcome);
        Assert.Null(await _fixture.Grains.GetGrain<IAgentLaunchCoordinatorGrain>(coordinatorKey)
            .ResumeExistingSpawnAsync(AgentLaunchCoordinatorCodec.SpawnFingerprint(targetAgentRef, prompt)));

        var mutationFence = await _fixture.Grains.GetGrain<ISessionTreeMutationFenceGrain>(projectId).GetAsync();
        Assert.DoesNotContain(
            mutationFence.Reservations ?? [],
            item => item.ParentSessionId == parentSessionId);

        await using var scope = _fixture.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        Assert.Equal(1, await db.AgentJobs.CountAsync(job => job.ProjectId == projectId));
    }

    private async Task OpenParentAsync(
        string projectId,
        string sessionId,
        string runnerId,
        AgentInfo target)
    {
        var parent = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await parent.OpenAsync(new OpenAgentSessionCommand(
            runnerId,
            "opencode",
            $"/workspace/{sessionId}",
            Metadata: Metadata(projectId, sessionId),
            Definition: new AgentExecutionDefinition(
                "parent instructions",
                "opencode",
                "gpt-5.6-luna",
                "xhigh",
                [],
                [new AllowedSubagentSnapshot(target.Id, target.Name, target.Description)])));
        await parent.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(
            $"runtime-{sessionId}",
            ExpectedRunnerId: runnerId,
            ExpectedRuntime: "opencode"));
    }

    private async Task<string> CreateProjectAsync(string prefix)
    {
        var name = $"{prefix}-{Guid.NewGuid():N}"[..Math.Min(63, prefix.Length + 33)];
        using var response = await _fixture.Client.PostAsJsonAsync("/api/projects", new
        {
            name,
            repository = new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main" },
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("data").GetProperty("id").GetString()!;
    }

    private async Task<AgentInfo> CreateAgentAsync(string projectId, string name)
    {
        using var response = await _fixture.Client.PostAsJsonAsync($"/api/projects/{projectId}/agents", new
        {
            name,
            description = $"description for {name}",
            instructions = $"instructions for {name}",
            agentConfig = new { model = "openai/gpt-5.6" },
            skills = new[] { "coding" },
            maxConcurrentRuns = 1,
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var agentId = body.GetProperty("data").GetProperty("id").GetString()!;
        await using var scope = _fixture.Services.CreateAsyncScope();
        var agent = await scope.ServiceProvider.GetRequiredService<AgentQuerier>()
            .GetByIdAsync(projectId, agentId);
        return agent!;
    }

    private async Task SeedCompletedTargetExecutionAsync(string projectId, AgentInfo agent)
    {
        var terminalAt = new DateTimeOffset(2026, 8, 5, 0, 0, 0, TimeSpan.Zero);
        await using var scope = _fixture.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        db.AgentJobs.Add(new AgentJobRow
        {
            JobKey = $"spawn-admission-target-history-{Guid.NewGuid():N}",
            State = JSON.Serialize(new AgentJobState
            {
                Status = AgentJobStatus.Completed,
                SubmittedAt = terminalAt,
                TerminalAt = terminalAt,
                Input = new AgentJobInput(
                    "previous target execution",
                    Model: "openai/gpt-5.6",
                    ProjectId: projectId,
                    Runtime: "opencode",
                    AgentId: agent.Id,
                    AgentInstructions: agent.Instructions,
                    Skills: agent.Skills),
            }),
            ProjectId = projectId,
            AgentId = agent.Id,
            Status = AgentJobStatus.Completed.ToString().ToLowerInvariant(),
            SubmittedAt = terminalAt.ToString("O"),
            TerminalAt = terminalAt.ToString("O"),
            LaunchVisibility = "visible",
        });
        await db.SaveChangesAsync();
    }

    private static AgentSessionMetadata Metadata(string projectId, string agentId) =>
        new(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
            [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
            [GenericAgentSessionMetadata.AgentId] = agentId,
            [GenericAgentSessionMetadata.AgentName] = agentId,
        });
}
