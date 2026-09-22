using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Auth.Domain;
using Mohist.Server.Auth.Identity;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.TestSupport;
using Mohist.Server.Tests.Agent.Grain;
using Mohist.Server.Tests.Support;
using Orleans.Reminders;
using Orleans.Runtime;
using Orleans.Storage;
using Xunit;

namespace Mohist.Server.Tests.Runner.Grain;

[Collection("AgentJobGrain")]
[Trait("level", "L0")]
public sealed class RunnerAdministrativeRemovalSpecs(AgentJobGrainFixture fixture)
{
    [Fact]
    public async Task IntentRecordedBeforeContinuationRecoversFromDurableReminderWithoutAnotherRevoke()
    {
        var runnerId = $"runner-removal-crash-{Guid.NewGuid():N}";
        var runner = fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
        var credential = await IssueCredentialAsync(runnerId);
        await runner.RegisterAsync(
            new RunnerInfo(runnerId, ["spec/*"], "test-host", null),
            "process-before-crash",
            Presented(credential));
        var observer = fixture.Cluster.GetSiloServiceProvider(null)
            .GetRequiredService<RunnerAdministrativeRemovalObserver>();
        var injected = new InvalidOperationException("intent recorded crash boundary");
        observer.IntentRecordedAsync = (_, _) => Task.FromException(injected);

        try
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                runner.RevokeExecutionAuthorityAsync(fixture.TimeProvider.GetUtcNow()));
            Assert.Equal(injected.Message, failure.Message);

            var storage = fixture.Cluster.GetSiloServiceProvider(null)
                .GetRequiredService<IGrainStorage>();
            var pending = new GrainState<RunnerState>();
            await storage.ReadStateAsync("runner", runner.GetGrainId(), pending);
            Assert.Equal(
                RunnerAdministrativeRemovalPhase.IntentRecorded,
                pending.State.AdministrativeRemoval!.Phase);
            var reminders = fixture.Cluster.GetSiloServiceProvider(null)
                .GetRequiredService<IReminderTable>();
            Assert.NotNull(await reminders.ReadRow(
                runner.GetGrainId(),
                "administrative-removal"));

            observer.Reset();
            await TestLifecycle.DeactivateAndWait(runner, fixture.Grains);
            runner = fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
            await runner.AsReference<IRemindable>().ReceiveReminder(
                "administrative-removal",
                default);

            var completed = new GrainState<RunnerState>();
            await storage.ReadStateAsync("runner", runner.GetGrainId(), completed);
            Assert.Equal(
                RunnerAdministrativeRemovalPhase.Completed,
                completed.State.AdministrativeRemoval!.Phase);
            Assert.Null(await reminders.ReadRow(
                runner.GetGrainId(),
                "administrative-removal"));
        }
        finally
        {
            observer.Reset();
        }
    }

    [Fact]
    public async Task FailedIntentWriteReplyReloadsCommittedIntentInsteadOfRollingItBack()
    {
        var runnerId = $"runner-removal-uncertain-{Guid.NewGuid():N}";
        var runner = fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
        var credential = await IssueCredentialAsync(runnerId);
        await runner.RegisterAsync(
            new RunnerInfo(runnerId, ["spec/*"], "test-host", null),
            "process-before-uncertain-write",
            Presented(credential));
        var observer = fixture.Cluster.GetSiloServiceProvider(null)
            .GetRequiredService<RunnerAdministrativeRemovalObserver>();
        observer.AfterIntentWriteAsync = (_, _) =>
            Task.FromException(new InvalidOperationException("write reply lost"));

        try
        {
            var result = await runner.RevokeExecutionAuthorityAsync(
                fixture.TimeProvider.GetUtcNow());

            Assert.True(result.Completed);
            var storage = fixture.Cluster.GetSiloServiceProvider(null)
                .GetRequiredService<IGrainStorage>();
            var state = new GrainState<RunnerState>();
            await storage.ReadStateAsync("runner", runner.GetGrainId(), state);
            Assert.Equal(
                RunnerAdministrativeRemovalPhase.Completed,
                state.State.AdministrativeRemoval!.Phase);
        }
        finally
        {
            observer.Reset();
        }
    }

    [Fact]
    public async Task PrearmedReminderWithoutIntentRemovesItself()
    {
        var runnerId = $"runner-removal-orphan-wake-{Guid.NewGuid():N}";
        var runner = fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
        _ = await runner.GetRuntimeStateAsync();
        var reminders = fixture.Cluster.GetSiloServiceProvider(null)
            .GetRequiredService<IReminderTable>();
        await reminders.UpsertRow(RemovalReminder(runner));

        await runner.AsReference<IRemindable>().ReceiveReminder(
            "administrative-removal",
            default);

        Assert.Null(await reminders.ReadRow(
            runner.GetGrainId(),
            "administrative-removal"));
    }

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

            var removedCredential = await IssueCredentialAsync(runnerId);
            await runner.RegisterAsync(
                new RunnerInfo(runnerId, ["spec/*"], "test-host", null),
                oldProcess,
                Presented(removedCredential));
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
            var storage = fixture.Cluster.GetSiloServiceProvider(null)
                .GetRequiredService<IGrainStorage>();
            var removalState = new GrainState<RunnerState>();
            await storage.ReadStateAsync("runner", runner.GetGrainId(), removalState);
            Assert.Equal(
                removedCredential.Credential.Id,
                removalState.State.AdministrativeRemoval!.RemovedCredentialId);

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

            var reminders = fixture.Cluster.GetSiloServiceProvider(null)
                .GetRequiredService<IReminderTable>();
            await reminders.UpsertRow(RemovalReminder(runner));
            await TestLifecycle.DeactivateAndWait(runner, fixture.Grains);
            runner = fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
            _ = await runner.GetRuntimeStateAsync();
            Assert.Null(await reminders.ReadRow(runner.GetGrainId(), "administrative-removal"));

            await Assert.ThrowsAsync<RunnerCredentialAuthorityException>(() => runner.RegisterAsync(
                new RunnerInfo(runnerId, ["spec/*"], "test-host", null),
                newProcess,
                Presented(removedCredential)));

            var replacementCredential = await IssueCredentialAsync(runnerId);
            Assert.NotEqual(
                removedCredential.Credential.Id,
                replacementCredential.Credential.Id);
            await BackdateActiveCredentialAsync(
                runnerId,
                fixture.TimeProvider.GetUtcNow().AddDays(-1));
            await Assert.ThrowsAsync<RunnerCredentialAuthorityException>(() => runner.RegisterAsync(
                new RunnerInfo(runnerId, ["spec/*"], "test-host", null),
                "stale-process-before-replacement-registers",
                Presented(removedCredential)));
            await Assert.ThrowsAsync<RunnerCredentialAuthorityException>(() => runner.RegisterAsync(
                new RunnerInfo(runnerId, ["spec/*"], "test-host", null),
                "missing-presented-authority",
                new RunnerPresentedAuthority(CredentialId: null, OperatorOverride: false)));
            await runner.RegisterAsync(
                new RunnerInfo(runnerId, ["spec/*"], "test-host", null),
                newProcess,
                Presented(replacementCredential));
            await Assert.ThrowsAsync<RunnerCredentialAuthorityException>(() => runner.RegisterAsync(
                new RunnerInfo(runnerId, ["spec/*"], "test-host", null),
                "stale-process-after-removal-clears",
                Presented(removedCredential)));

            await TestLifecycle.DeactivateAndWait(runner, fixture.Grains);
            runner = fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
            Assert.True(await runner.IsCurrentRegistrationAuthorityAsync(
                newProcess,
                Presented(replacementCredential)));
            Assert.False(await runner.IsCurrentRegistrationAuthorityAsync(
                newProcess,
                Presented(removedCredential)));
            Assert.True(await runner.IsCurrentProcessGenerationAsync(newProcess));
            Assert.False((await runner.GetRuntimeStateAsync()).Draining);

            await reminders.UpsertRow(RemovalReminder(runner));
            await runner.AsReference<IRemindable>().ReceiveReminder("administrative-removal", default);
            Assert.Null(await reminders.ReadRow(runner.GetGrainId(), "administrative-removal"));
        }
        finally
        {
            fixture.SessionStatePersistence.Reset();
        }
    }

    private static RunnerPresentedAuthority Presented(RunnerCredentialCreateResult credential) =>
        new(credential.Credential.Id, OperatorOverride: false);

    private static ReminderEntry RemovalReminder(IRunnerGrain runner) => new()
    {
        GrainId = runner.GetGrainId(),
        ReminderName = "administrative-removal",
        StartAt = TestTime.UtcNow.UtcDateTime,
        Period = TimeSpan.FromDays(1),
    };

    private async Task BackdateActiveCredentialAsync(string runnerId, DateTimeOffset issuedAt)
    {
        using var scope = fixture.Cluster.GetSiloServiceProvider(null).CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var row = await db.Credentials.SingleAsync(candidate =>
            candidate.Kind.ToLower() == "runner"
            && candidate.Name == runnerId
            && candidate.RevokedAt == null);
        row.CreatedAt = issuedAt;
        await db.SaveChangesAsync();
    }

    private async Task<RunnerCredentialCreateResult> IssueCredentialAsync(string runnerId)
    {
        using var scope = fixture.Cluster.GetSiloServiceProvider(null).CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<ICredentialStore>()
            .CreateRunnerCredentialAsync(MohistPrincipal.AdminPrincipalId, runnerId);
        return Assert.IsType<RunnerCredentialCreateResult>(result);
    }
}
