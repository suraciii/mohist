using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Services;

public sealed partial class AgentSpawnAdmissionLifecycleSpecs
{
    [Fact]
    public async Task FrozenMembershipSpawn_RetryStaysPending_NewFingerprintConflicts_AdvancesAfterStopTerminal()
    {
        var projectId = await CreateProjectAsync("spawn-admission-stop-retry");
        var target = await CreateAgentAsync(projectId, "spawn-admission-retry-target");
        await SeedCompletedTargetExecutionAsync(projectId, target);
        var runnerId = $"spawn-admission-retry-runner-{Guid.NewGuid():N}";
        await _fixture.Grains.GetGrain<IRunnerGrain>(runnerId).RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "spawn-admission-retry-host",
            projectId));

        try
        {
            var parentId = $"spawn-admission-retry-parent-{Guid.NewGuid():N}";
            await OpenParentAsync(projectId, parentId, runnerId, target);

            var fence = _fixture.Grains.GetGrain<ISessionTreeMutationFenceGrain>(projectId);
            var operationId = $"stop-operation-{Guid.NewGuid():N}";
            var stop = await fence.BeginStopSnapshotAsync(new BeginSessionTreeStopSnapshotCommand(
                projectId,
                parentId,
                operationId,
                $"stop-input-{Guid.NewGuid():N}",
                "stop-fingerprint"));
            Assert.Equal(SessionTreeStopSnapshotDisposition.Started, stop.Disposition);

            var idempotencyKey = $"retry-key-{Guid.NewGuid():N}";
            const string prompt = "retry while frozen";
            for (var attempt = 0; attempt < 2; attempt++)
            {
                AgentSpawnValidationPendingException pending;
                await using (var scope = _fixture.Services.CreateAsyncScope())
                {
                    var launcher = scope.ServiceProvider.GetRequiredService<IAgentLauncher>();
                    pending = await Assert.ThrowsAsync<AgentSpawnValidationPendingException>(() =>
                        launcher.LaunchSubagentAsync(projectId, parentId, target.Id, prompt, idempotencyKey));
                }
                Assert.Equal("parent_tree_stop_in_progress", pending.Reason);
                await AssertNoSpawnArtifactsAsync(projectId, parentId, idempotencyKey, target.Id, prompt);
            }

            await using (var scope = _fixture.Services.CreateAsyncScope())
            {
                var launcher = scope.ServiceProvider.GetRequiredService<IAgentLauncher>();
                await Assert.ThrowsAsync<LaunchIdempotencyConflictException>(() =>
                    launcher.LaunchSubagentAsync(
                        projectId,
                        parentId,
                        target.Id,
                        "different prompt under the same key",
                        idempotencyKey));
            }

            var terminal = await fence.SetStopAdmissionAsync(
                operationId,
                SessionTreeStopAdmissionOutcome.Completed);
            Assert.False(terminal.Active);

            AgentLaunchResult result;
            await using (var scope = _fixture.Services.CreateAsyncScope())
            {
                var launcher = scope.ServiceProvider.GetRequiredService<IAgentLauncher>();
                result = await launcher.LaunchSubagentAsync(
                    projectId,
                    parentId,
                    target.Id,
                    prompt,
                    idempotencyKey);
            }
            Assert.Equal(target.Id, result.AgentId);
            var state = await fence.GetAsync();
            Assert.Equal(
                LinkReservationState.Attached,
                state.Reservations!.Single(item => item.ChildSessionId == result.SessionId).State);
            var requestFence = await _fixture.Grains.GetGrain<ISpawnRequestFenceGrain>(
                    AgentLaunchCoordinatorCodec.KeyFor(projectId, parentId, idempotencyKey))
                .GetAsync();
            Assert.Equal(SpawnRequestFenceOutcome.Admitted, requestFence!.Outcome);
        }
        finally
        {
            await _fixture.Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global)
                .UnregisterAsync(runnerId);
        }
    }

    [Fact]
    public async Task MaterializingStopSnapshot_KeepsSpawnValidationPending_UntilFrozenOutsideMembershipAllows()
    {
        var projectId = await CreateProjectAsync("spawn-admission-stop-materializing");
        var target = await CreateAgentAsync(projectId, "spawn-admission-materializing-target");
        await SeedCompletedTargetExecutionAsync(projectId, target);
        var runnerId = $"spawn-admission-materializing-runner-{Guid.NewGuid():N}";
        await _fixture.Grains.GetGrain<IRunnerGrain>(runnerId).RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "spawn-admission-materializing-host",
            projectId));

        try
        {
            var parentId = $"spawn-admission-materializing-parent-{Guid.NewGuid():N}";
            await OpenParentAsync(projectId, parentId, runnerId, target);

            var fence = _fixture.Grains.GetGrain<ISessionTreeMutationFenceGrain>(projectId);
            var command = new BeginSessionTreeStopSnapshotCommand(
                projectId,
                $"spawn-admission-materializing-missing-root-{Guid.NewGuid():N}",
                $"stop-operation-{Guid.NewGuid():N}",
                $"stop-input-{Guid.NewGuid():N}",
                "stop-fingerprint");
            await Assert.ThrowsAsync<InvalidOperationException>(() => fence.BeginStopSnapshotAsync(command));
            Assert.Equal(
                SessionTreeStopSnapshotPhase.Materializing,
                Assert.Single((await fence.GetAsync()).StopSnapshots!).Phase);

            var idempotencyKey = $"materializing-key-{Guid.NewGuid():N}";
            AgentSpawnValidationPendingException pending;
            await using (var scope = _fixture.Services.CreateAsyncScope())
            {
                var launcher = scope.ServiceProvider.GetRequiredService<IAgentLauncher>();
                pending = await Assert.ThrowsAsync<AgentSpawnValidationPendingException>(() =>
                    launcher.LaunchSubagentAsync(
                        projectId,
                        parentId,
                        target.Id,
                        "spawn during materializing",
                        idempotencyKey));
            }
            Assert.Equal("parent_tree_stop_in_progress", pending.Reason);
            await AssertNoSpawnArtifactsAsync(
                projectId,
                parentId,
                idempotencyKey,
                target.Id,
                "spawn during materializing");

            await OpenParentAsync(projectId, command.RootSessionId, runnerId, target);
            var recovered = await fence.BeginStopSnapshotAsync(command);
            Assert.Equal(SessionTreeStopSnapshotDisposition.Started, recovered.Disposition);
            Assert.Equal(SessionTreeStopSnapshotPhase.Frozen, recovered.Snapshot!.Phase);

            AgentLaunchResult result;
            await using (var scope = _fixture.Services.CreateAsyncScope())
            {
                var launcher = scope.ServiceProvider.GetRequiredService<IAgentLauncher>();
                result = await launcher.LaunchSubagentAsync(
                    projectId,
                    parentId,
                    target.Id,
                    "spawn during materializing",
                    idempotencyKey);
            }
            Assert.Equal(target.Id, result.AgentId);
            Assert.Equal(
                LinkReservationState.Attached,
                (await fence.GetAsync()).Reservations!
                    .Single(item => item.ChildSessionId == result.SessionId).State);
        }
        finally
        {
            await _fixture.Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global)
                .UnregisterAsync(runnerId);
        }
    }

    [Fact]
    public async Task AdmittedPlanBeforeStopPublish_RejectedReservation_ConvergesToDurablePostPlanRejection()
    {
        var projectId = await CreateProjectAsync("spawn-admission-postplan-stop");
        var target = await CreateAgentAsync(projectId, "spawn-admission-postplan-target");
        await SeedCompletedTargetExecutionAsync(projectId, target);
        var runnerId = $"spawn-admission-postplan-runner-{Guid.NewGuid():N}";
        await _fixture.Grains.GetGrain<IRunnerGrain>(runnerId).RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "spawn-admission-postplan-host",
            projectId));

        var idempotencyKey = $"postplan-key-{Guid.NewGuid():N}";
        const string prompt = "admitted before stop";
        try
        {
            var parentId = $"spawn-admission-postplan-parent-{Guid.NewGuid():N}";
            await OpenParentAsync(projectId, parentId, runnerId, target);

            _fixture.LaunchFaults.FailNext(LaunchParticipantGate.ReserveLink);
            await using (var scope = _fixture.Services.CreateAsyncScope())
            {
                var launcher = scope.ServiceProvider.GetRequiredService<IAgentLauncher>();
                await Assert.ThrowsAsync<LaunchSetupPendingException>(() =>
                    launcher.LaunchSubagentAsync(projectId, parentId, target.Id, prompt, idempotencyKey));
            }

            var fence = _fixture.Grains.GetGrain<ISessionTreeMutationFenceGrain>(projectId);
            var stop = await fence.BeginStopSnapshotAsync(new BeginSessionTreeStopSnapshotCommand(
                projectId,
                parentId,
                $"stop-operation-{Guid.NewGuid():N}",
                $"stop-input-{Guid.NewGuid():N}",
                "stop-fingerprint"));
            Assert.Equal(SessionTreeStopSnapshotDisposition.Started, stop.Disposition);

            for (var attempt = 0; attempt < 2; attempt++)
            {
                AgentSpawnPostPlanRejectedException rejected;
                await using (var scope = _fixture.Services.CreateAsyncScope())
                {
                    var launcher = scope.ServiceProvider.GetRequiredService<IAgentLauncher>();
                    rejected = await Assert.ThrowsAsync<AgentSpawnPostPlanRejectedException>(() =>
                        launcher.LaunchSubagentAsync(projectId, parentId, target.Id, prompt, idempotencyKey));
                }
                Assert.Equal("parent_link_rejected", rejected.Reason);
            }

            var state = await fence.GetAsync();
            Assert.Equal(
                LinkReservationState.Rejected,
                state.Reservations!.Single(item => item.ParentSessionId == parentId).State);

            await using var verifyScope = _fixture.Services.CreateAsyncScope();
            var factory = verifyScope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            var jobs = await db.AgentJobs
                .Where(job => job.ProjectId == projectId)
                .ToListAsync();
            var childJob = Assert.Single(
                jobs,
                job => job.JobKey.StartsWith("agent-job-launch-", StringComparison.Ordinal));
            Assert.Equal("cancelled", childJob.Status);
        }
        finally
        {
            _fixture.LaunchFaults.StopFailing(LaunchParticipantGate.ReserveLink);
            await _fixture.Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global)
                .UnregisterAsync(runnerId);
        }
    }

    [Fact]
    public async Task ReconciliationRequiredFence_RejectsSpawnPreplan_NoArtifacts_StableReplay()
    {
        var projectId = await CreateProjectAsync("spawn-admission-reconciliation");
        var target = await CreateAgentAsync(projectId, "spawn-admission-reconciliation-target");
        await SeedCompletedTargetExecutionAsync(projectId, target);
        var runnerId = $"spawn-admission-reconciliation-runner-{Guid.NewGuid():N}";
        await _fixture.Grains.GetGrain<IRunnerGrain>(runnerId).RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "spawn-admission-reconciliation-host",
            projectId));

        try
        {
            var parentId = $"spawn-admission-reconciliation-parent-{Guid.NewGuid():N}";
            await OpenParentAsync(projectId, parentId, runnerId, target);

            var fence = _fixture.Grains.GetGrain<ISessionTreeMutationFenceGrain>(projectId);
            var reconciled = await fence.AcknowledgeFinalizeAsync(new SessionTreeAttachReceipt(
                "command-reconciled",
                "edge-reconciled",
                "parent-reconciled",
                "child-reconciled",
                "job-reconciled",
                1,
                "wrong-project"));
            Assert.True(reconciled.ReconciliationRequired);
            Assert.True((await fence.GetAsync()).ReconciliationRequired);

            var idempotencyKey = $"reconciliation-key-{Guid.NewGuid():N}";
            const string prompt = "spawn while fence reconciled";
            for (var attempt = 0; attempt < 2; attempt++)
            {
                AgentSpawnPreplanRejectedException rejected;
                await using (var scope = _fixture.Services.CreateAsyncScope())
                {
                    var launcher = scope.ServiceProvider.GetRequiredService<IAgentLauncher>();
                    rejected = await Assert.ThrowsAsync<AgentSpawnPreplanRejectedException>(() =>
                        launcher.LaunchSubagentAsync(projectId, parentId, target.Id, prompt, idempotencyKey));
                }
                Assert.Equal("session_tree_reconciliation_required", rejected.Reason);
            }

            var coordinatorKey = AgentLaunchCoordinatorCodec.KeyFor(projectId, parentId, idempotencyKey);
            var requestFence = await _fixture.Grains.GetGrain<ISpawnRequestFenceGrain>(coordinatorKey).GetAsync();
            Assert.NotNull(requestFence);
            Assert.Equal(SpawnRequestFenceOutcome.PreplanRejected, requestFence!.Outcome);
            Assert.Equal("session_tree_reconciliation_required", requestFence.PreplanRejectionReason);
            Assert.Null(await _fixture.Grains.GetGrain<IAgentLaunchCoordinatorGrain>(coordinatorKey)
                .ResumeExistingSpawnAsync(AgentLaunchCoordinatorCodec.SpawnFingerprint(target.Id, prompt)));
            Assert.DoesNotContain(
                (await fence.GetAsync()).Reservations ?? [],
                item => item.ParentSessionId == parentId);

            await using var verifyScope = _fixture.Services.CreateAsyncScope();
            var factory = verifyScope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            Assert.Equal(1, await db.AgentJobs.CountAsync(job => job.ProjectId == projectId));
        }
        finally
        {
            await _fixture.Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global)
                .UnregisterAsync(runnerId);
        }
    }

}
