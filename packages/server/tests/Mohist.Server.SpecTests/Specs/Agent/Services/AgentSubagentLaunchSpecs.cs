using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Services;

[Collection("IntegrationRunner")]
public sealed class AgentSubagentLaunchSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public AgentSubagentLaunchSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task LaunchSubagent_PersistsAdmissionRunnerPinAndStartupSnapshot()
    {
        var projectId = await CreateProjectAsync("launcher-subagent-snapshot");
        var allowed = await CreateAgentAsync(projectId, "allowed-child");
        var target = await CreateAgentAsync(projectId, "target-child");
        using (var update = await _fixture.Client.PatchAsJsonAsync(
                   $"/api/projects/{projectId}/agents/{target.Id}",
                   new { allowedSubagentAgentIds = new[] { allowed.Id } }))
        {
            update.EnsureSuccessStatusCode();
        }
        await SeedCompletedTargetExecutionAsync(projectId, target);

        var runnerId = $"launcher-subagent-runner-{Guid.NewGuid():N}";
        var otherRunnerId = $"launcher-subagent-other-runner-{Guid.NewGuid():N}";
        await _fixture.Grains.GetGrain<IRunnerGrain>(runnerId).RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "launcher-subagent-host",
            projectId));
        await _fixture.Grains.GetGrain<IRunnerGrain>(otherRunnerId).RegisterAsync(new RunnerInfo(
            otherRunnerId,
            ["spec/*"],
            "launcher-subagent-host",
            projectId));

        try
        {
            var parentSessionId = $"launcher-subagent-parent-{Guid.NewGuid():N}";
            var parent = _fixture.Grains.GetGrain<IAgentSessionGrain>(parentSessionId);
            await parent.OpenAsync(new OpenAgentSessionCommand(
                RunnerId: runnerId,
                AgentRuntime: "opencode",
                WorkDir: "/workspace/launcher-subagent",
                Metadata: SpawnMetadata(projectId, "agent-parent"),
                Definition: new AgentExecutionDefinition(
                    "parent instructions",
                    "opencode",
                    "gpt-5.6-luna",
                    "xhigh",
                    [],
                    [new AllowedSubagentSnapshot(target.Id, target.Name, target.Description)])));
            await parent.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(
                "parent-runtime",
                ExpectedRunnerId: runnerId,
                ExpectedRuntime: "opencode"));

            var idempotencyKey = $"launcher-subagent-key-{Guid.NewGuid():N}";
            AgentLaunchResult result;
            await using (var scope = _fixture.Services.CreateAsyncScope())
            {
                var launcher = scope.ServiceProvider.GetRequiredService<IAgentLauncher>();
                result = await launcher.LaunchSubagentAsync(
                    projectId,
                    parentSessionId,
                    target.Id,
                    "launch from the admitted snapshot",
                    idempotencyKey);
            }

            await using var verifyScope = _fixture.Services.CreateAsyncScope();
            var store = verifyScope.ServiceProvider.GetRequiredService<IAgentJobStore>();
            var ledger = await store.LoadLedgerAsync(result.JobKey);
            Assert.NotNull(ledger);
            Assert.Equal(runnerId, ledger!.PinnedRunnerId);
            Assert.Equal(runnerId, ledger.AssignedRunnerId);
            Assert.Empty(await store.ListAssignedPendingForRunnerAsync(otherRunnerId, 10));

            var dispatch = JsonSerializer.Deserialize<WorkDispatch>(ledger.DispatchJson!, JSON.Options);
            Assert.NotNull(dispatch);
            Assert.Equal(target.Id, dispatch!.AgentId);
            Assert.Equal(runnerId, dispatch.PinnedRunnerId);
            var startup = dispatch.AgentSessionStartup
                ?? throw new Xunit.Sdk.XunitException("AgentSessionStartup was not persisted in the dispatch projection.");
            Assert.Equal(projectId, startup.ProjectId);
            Assert.Equal(result.SessionId, startup.SessionId);
            Assert.Equal(parentSessionId, startup.ParentSessionId);
            var allowedSnapshot = Assert.Single(startup.AllowedSubagents);
            Assert.Equal(allowed.Id, allowedSnapshot.AgentId);
            Assert.Equal(allowed.Name, allowedSnapshot.NameAtLaunch);
            Assert.Equal(allowed.Description, allowedSnapshot.DescriptionAtLaunch);
            Assert.Equal(target.Id, startup.AgentId);
            Assert.Equal(target.Name, startup.AgentName);
            Assert.Contains($"mo agent spawn {target.Id}", startup.SpawnCommand, StringComparison.Ordinal);
            Assert.Equal("/workspace/launcher-subagent", startup.WorkDir);
            Assert.Equal(runnerId, startup.PinnedRunnerId);
        }
        finally
        {
            await _fixture.Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global).UnregisterAsync(runnerId);
            await _fixture.Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global).UnregisterAsync(otherRunnerId);
        }
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
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("data").GetProperty("id").GetString()
            ?? throw new InvalidOperationException($"CreateProject '{name}' returned no id");
    }

    private async Task<AgentInfo> CreateAgentAsync(string projectId, string name)
    {
        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/agents",
            new
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
        var querier = scope.ServiceProvider.GetRequiredService<AgentQuerier>();
        return await querier.GetByIdAsync(projectId, agentId)
            ?? throw new InvalidOperationException($"Agent '{agentId}' was not found after creation");
    }

    private async Task SeedCompletedTargetExecutionAsync(string projectId, AgentInfo agent)
    {
        var terminalAt = new DateTimeOffset(2026, 8, 5, 0, 0, 0, TimeSpan.Zero);
        var (model, variant) = AgentLauncher.ResolveModelAndVariant(agent.AgentConfig);
        await using var scope = _fixture.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        db.AgentJobs.Add(new AgentJobRow
        {
            JobKey = $"launcher-target-history-{Guid.NewGuid():N}",
            State = JSON.Serialize(new AgentJobState
            {
                Status = AgentJobStatus.Completed,
                SubmittedAt = terminalAt,
                TerminalAt = terminalAt,
                Input = new AgentJobInput(
                    "previous target execution",
                    Model: model,
                    ProjectId: projectId,
                    Runtime: AgentLauncher.ResolveRuntime(agent.AgentConfig),
                    AgentId: agent.Id,
                    AgentInstructions: agent.Instructions,
                    Variant: variant,
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

    private static AgentSessionMetadata SpawnMetadata(string projectId, string agentId) =>
        new(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
            [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
            [GenericAgentSessionMetadata.AgentId] = agentId,
            [GenericAgentSessionMetadata.AgentName] = agentId,
        });
}
