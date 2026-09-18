using Mohist.Server.Runner.Grains;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Runner.Grain;

[Collection("ComponentGrain")]
[Trait("level", "L0")]
public sealed class RunnerEnvironmentApplicationSpecs
{
    private readonly ComponentWorkflowGrainFixture _fixture;

    public RunnerEnvironmentApplicationSpecs(ComponentWorkflowGrainFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Begin_UsesOneEnvironmentFenceAndReplacementRegistrationCannotReleaseIt()
    {
        var runnerId = $"runner-environment-application-{Guid.NewGuid():N}";
        var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
        var updateId = Guid.NewGuid().ToString("N");
        try
        {
            await runner.RegisterAsync(
                new RunnerInfo(
                    runnerId,
                    ["spec/*"],
                    "environment-application-host",
                    null,
                    EnvironmentVersion: "old-version",
                    ConnectionGeneration: "connection-a"),
                "generation-a");

            var begin = await runner.BeginEnvironmentApplicationAsync(
                updateId,
                "new-version",
                "generation-a",
                "connection-a");

            Assert.NotNull(begin);
            Assert.Equal(RunnerEnvironmentApplicationBeginStatus.Waiting, begin!.Status);
            Assert.Equal(RunnerEnvironmentApplicationPhase.Waiting, begin.Application!.Phase);
            Assert.True((await runner.GetRuntimeStateAsync()).Draining);

            var staleBegin = await runner.BeginEnvironmentApplicationAsync(
                Guid.NewGuid().ToString("N"),
                "other-version",
                "old-generation",
                "connection-a");
            Assert.Equal(RunnerEnvironmentApplicationBeginStatus.NotReady, staleBegin!.Status);

            await runner.RegisterAsync(
                new RunnerInfo(
                    runnerId,
                    ["spec/*"],
                    "environment-application-host",
                    null,
                    EnvironmentVersion: "new-version",
                    ConnectionGeneration: "connection-b"),
                "generation-b");

            var afterReplacement = await runner.GetEnvironmentApplicationAsync(updateId);
            Assert.Equal(RunnerEnvironmentApplicationPhase.Waiting, afterReplacement.Application!.Phase);
            Assert.Equal(updateId, (await runner.GetRuntimeStateAsync()).UpdateInterruptId);
            Assert.True((await runner.GetRuntimeStateAsync()).Draining);

            var releaseAttempt = await runner.BeginUpdateInterruptAsync(Guid.NewGuid().ToString("N"));
            Assert.Equal(RunnerUpdateInterruptBeginStatus.Superseded, releaseAttempt!.Status);
            Assert.Equal(updateId, releaseAttempt.UpdateInterruptId);
        }
        finally
        {
            await runner.UnregisterAsync();
        }
    }

    [Fact]
    public async Task ApplyRequiresCurrentSettledObservationAndRejectsStaleGeneration()
    {
        var runnerId = $"runner-environment-apply-{Guid.NewGuid():N}";
        var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
        var updateId = Guid.NewGuid().ToString("N");
        try
        {
            await runner.RegisterAsync(
                new RunnerInfo(
                    runnerId,
                    ["spec/*"],
                    "environment-apply-host",
                    null,
                    EnvironmentVersion: "old-version",
                    ConnectionGeneration: "connection-a"),
                "generation-a");
            await runner.ObserveDispatchObservationAsync(
                "generation-a",
                new RunnerDispatchObservation(
                    "connection-a",
                    AdmissionReady: true,
                    AdmissionReasonCodes: [],
                    RuntimeReadiness: [],
                    ProcessGeneration: "generation-a",
                    InFlightCount: 1,
                    AwaitingAckCount: 0));
            Assert.Equal(
                RunnerEnvironmentApplicationCommandStatus.NotFound,
                (await runner.BeginEnvironmentApplyAsync(updateId, "generation-a")).Status);

            await runner.BeginEnvironmentApplicationAsync(updateId, "new-version", "generation-a", "connection-a");
            var unsettled = await runner.BeginEnvironmentApplyAsync(updateId, "generation-a");
            Assert.Equal(RunnerEnvironmentApplicationCommandStatus.NotSettled, unsettled.Status);
            Assert.False(unsettled.Application!.Settlement.Settled);

            var stale = await runner.BeginEnvironmentApplyAsync(updateId, "old-generation");
            Assert.Equal(RunnerEnvironmentApplicationCommandStatus.Stale, stale.Status);

            await runner.ObserveDispatchObservationAsync(
                "generation-a",
                new RunnerDispatchObservation(
                    "connection-a",
                    AdmissionReady: true,
                    AdmissionReasonCodes: [],
                    RuntimeReadiness: [],
                    ProcessGeneration: "generation-a"));
            var applying = await runner.BeginEnvironmentApplyAsync(updateId, "generation-a");
            Assert.Equal(RunnerEnvironmentApplicationCommandStatus.Accepted, applying.Status);
            Assert.Equal(RunnerEnvironmentApplicationPhase.Applying, applying.Application!.Phase);
            Assert.True(applying.Application.Settlement.Settled);

            await runner.RegisterAsync(
                new RunnerInfo(
                    runnerId,
                    ["spec/*"],
                    "environment-apply-host",
                    null,
                    EnvironmentVersion: "old-version",
                    ConnectionGeneration: "connection-b"),
                "generation-b");
            var staleRetry = await runner.BeginEnvironmentApplyAsync(updateId, "generation-a");
            Assert.Equal(RunnerEnvironmentApplicationCommandStatus.Stale, staleRetry.Status);
        }
        finally
        {
            await runner.UnregisterAsync();
        }
    }

    [Fact]
    public async Task ConfirmRequiresTargetWitnessAndOnlyThenReleasesTheFence()
    {
        var runnerId = $"runner-environment-confirm-{Guid.NewGuid():N}";
        var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
        var updateId = Guid.NewGuid().ToString("N");
        try
        {
            await runner.RegisterAsync(
                new RunnerInfo(
                    runnerId,
                    ["spec/*"],
                    "environment-confirm-host",
                    null,
                    EnvironmentVersion: "old-version",
                    ConnectionGeneration: "connection-a"),
                "generation-a");
            await runner.ObserveDispatchObservationAsync(
                "generation-a",
                new RunnerDispatchObservation(
                    "connection-a", true, [], [], ProcessGeneration: "generation-a"));
            await runner.BeginEnvironmentApplicationAsync(updateId, "new-version", "generation-a", "connection-a");
            await runner.BeginEnvironmentApplyAsync(updateId, "generation-a");

            var wrongVersion = await runner.ConfirmEnvironmentApplicationAsync(
                updateId,
                "generation-a",
                "old-version");
            Assert.Equal(RunnerEnvironmentApplicationCommandStatus.VersionMismatch, wrongVersion.Status);

            await runner.RegisterAsync(
                new RunnerInfo(
                    runnerId,
                    ["spec/*"],
                    "environment-confirm-host",
                    null,
                    EnvironmentVersion: "new-version",
                    ConnectionGeneration: "connection-b"),
                "generation-b");

            var staleGeneration = await runner.ConfirmEnvironmentApplicationAsync(
                updateId,
                "generation-a",
                "new-version");
            Assert.Equal(RunnerEnvironmentApplicationCommandStatus.Stale, staleGeneration.Status);

            var confirmed = await runner.ConfirmEnvironmentApplicationAsync(
                updateId,
                "generation-b",
                "new-version");
            Assert.Equal(RunnerEnvironmentApplicationCommandStatus.Accepted, confirmed.Status);
            Assert.Equal(RunnerEnvironmentApplicationPhase.Active, confirmed.Application!.Phase);
            Assert.Null((await runner.GetRuntimeStateAsync()).UpdateInterruptId);
            Assert.False((await runner.GetRuntimeStateAsync()).Draining);
        }
        finally
        {
            await runner.UnregisterAsync();
        }
    }

    [Fact]
    public async Task FailureRequiresRollbackWitnessOrLeavesAnUnconfirmedFence()
    {
        var runnerId = $"runner-environment-recovery-{Guid.NewGuid():N}";
        var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
        var updateId = Guid.NewGuid().ToString("N");
        try
        {
            await runner.RegisterAsync(
                new RunnerInfo(
                    runnerId,
                    ["spec/*"],
                    "environment-recovery-host",
                    null,
                    EnvironmentVersion: "old-version",
                    ConnectionGeneration: "connection-a"),
                "generation-a");
            await runner.ObserveDispatchObservationAsync(
                "generation-a",
                new RunnerDispatchObservation(
                    "connection-a", true, [], [], ProcessGeneration: "generation-a"));
            await runner.BeginEnvironmentApplicationAsync(updateId, "new-version", "generation-a", "connection-a");
            await runner.BeginEnvironmentApplyAsync(updateId, "generation-a");
            Assert.Equal(
                RunnerEnvironmentApplicationCommandStatus.Failed,
                (await runner.FailEnvironmentApplicationAsync(updateId, "restart-failed")).Status);

            var wrongRollback = await runner.ConfirmEnvironmentRollbackAsync(updateId, "generation-a", "new-version");
            Assert.Equal(RunnerEnvironmentApplicationCommandStatus.VersionMismatch, wrongRollback.Status);

            var unconfirmed = await runner.MarkEnvironmentApplicationUnconfirmedAsync(updateId, "rollback-unconfirmed");
            Assert.Equal(RunnerEnvironmentApplicationCommandStatus.Unconfirmed, unconfirmed.Status);
            Assert.True((await runner.GetRuntimeStateAsync()).Draining);

            await runner.RegisterAsync(
                new RunnerInfo(
                    runnerId,
                    ["spec/*"],
                    "environment-recovery-host",
                    null,
                    EnvironmentVersion: "old-version",
                    ConnectionGeneration: "connection-b"),
                "generation-b");
            var rollback = await runner.ConfirmEnvironmentRollbackAsync(updateId, "generation-b", "old-version");
            Assert.Equal(RunnerEnvironmentApplicationCommandStatus.Accepted, rollback.Status);
            Assert.Equal(RunnerEnvironmentApplicationPhase.Failed, rollback.Application!.Phase);
            Assert.Null((await runner.GetRuntimeStateAsync()).UpdateInterruptId);

            var rollbackRetry = await runner.ConfirmEnvironmentRollbackAsync(updateId, "generation-b", "old-version");
            Assert.Equal(RunnerEnvironmentApplicationCommandStatus.AlreadyRolledBack, rollbackRetry.Status);

            var replay = await runner.BeginEnvironmentApplicationAsync(
                updateId,
                "another-version",
                "generation-b",
                "connection-b");
            Assert.Equal(RunnerEnvironmentApplicationBeginStatus.AlreadyCompleted, replay!.Status);
            Assert.Equal("new-version", replay.Application!.TargetVersion);
        }
        finally
        {
            await runner.UnregisterAsync();
        }
    }
}
