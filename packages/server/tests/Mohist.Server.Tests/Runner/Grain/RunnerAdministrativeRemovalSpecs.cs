using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Auth.Domain;
using Mohist.Server.Auth.Identity;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.TestSupport;
using Mohist.Server.Tests.Agent.Grain;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Runner.Grain;

[Collection("AgentJobGrain")]
[Trait("level", "L0")]
public sealed class RunnerAdministrativeRemovalSpecs(AgentJobGrainFixture fixture)
{
    [Fact]
    public async Task SessionPersistenceFailureRecoversAfterRunnerReloadAndReenrollment()
    {
        fixture.SessionStatePersistence.Reset();
        try
        {
            var runnerId = $"runner-removal-{Guid.NewGuid():N}";
            var sessionId = $"session-removal-{Guid.NewGuid():N}";
            var oldProcess = $"process-old-{Guid.NewGuid():N}";
            var newProcess = $"process-new-{Guid.NewGuid():N}";
            var runner = fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
            var session = fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);

            await IssueCredentialAsync(runnerId);
            await runner.RegisterAsync(
                new RunnerInfo(runnerId, ["spec/*"], "test-host", null),
                oldProcess);
            await session.OpenAsync(new OpenAgentSessionCommand(
                runnerId,
                "opencode",
                WorkDir: "/work",
                Metadata: GenericAgentSessionMetadata.Metadata(
                    new GenericAgentSessionContext("project-1", "agent-1", "Agent One"))));
            await session.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand("runtime-session-1"));
            await session.EnsureInitialLaunchAsync(new EnsureInitialLaunchCommand(
                "input-1", "turn-1", "prompt", "agent-connection", "job-1"));
            await session.MarkInitialTurnExecutingAsync("job-1");

            fixture.SessionStatePersistence.QueueFailures(1);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                runner.RevokeExecutionAuthorityAsync(fixture.TimeProvider.GetUtcNow()));
            Assert.False(await runner.IsCurrentProcessGenerationAsync(oldProcess));
            Assert.True((await runner.GetRuntimeStateAsync()).Draining);

            await TestLifecycle.DeactivateAndWait(runner, fixture.Grains);
            runner = fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
            Assert.False(await runner.IsCurrentProcessGenerationAsync(oldProcess));
            Assert.Equal("idle", (await session.GetAsync())!.Status);
            using (var scope = fixture.Cluster.GetSiloServiceProvider(null).CreateScope())
            {
                var persisted = await scope.ServiceProvider.GetRequiredService<AgentSessionStore>()
                    .LoadAsync(sessionId);
                Assert.Equal(runnerId, persisted!.Status.MissingRunnerFact!.RunnerId);
            }

            await IssueCredentialAsync(runnerId);
            await runner.RegisterAsync(
                new RunnerInfo(runnerId, ["spec/*"], "test-host", null),
                newProcess);

            Assert.True(await runner.IsCurrentProcessGenerationAsync(newProcess));
            Assert.False((await runner.GetRuntimeStateAsync()).Draining);
        }
        finally
        {
            fixture.SessionStatePersistence.Reset();
        }
    }

    private async Task IssueCredentialAsync(string runnerId)
    {
        using var scope = fixture.Cluster.GetSiloServiceProvider(null).CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<ICredentialStore>()
            .CreateRunnerCredentialAsync(MohistPrincipal.AdminPrincipalId, runnerId);
        Assert.NotNull(result);
    }
}
