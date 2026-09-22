using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Agent.Subscriptions;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Grains;
using Orleans;
using Orleans.Core.Internal;
using Xunit;

namespace Mohist.Server.Tests.Agent.Grain;

[Collection("AgentJobGrain")]
[Trait("level", "L1")]
public sealed class AgentJobActivityConvergenceSpecs : AgentJobGrainTestSupport
{
    public AgentJobActivityConvergenceSpecs(AgentJobGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task DurableSessionFact_FinalizesOwningJobAsUnknownWithoutCallbackOrReplay()
    {
        var jobId = $"activity-job-{Guid.NewGuid():N}";
        var sessionId = $"activity-session-{Guid.NewGuid():N}";
        var inputId = $"activity-input-{Guid.NewGuid():N}";
        var turnId = $"activity-turn-{Guid.NewGuid():N}";
        var job = JobGrain(jobId);
        await job.PrepareManualLaunchAsync(new PrepareManualLaunchCommand(
            sessionId,
            inputId,
            turnId,
            "do the work once",
            ProjectId: "activity-project",
            AgentId: "activity-agent",
            ConnectionOrigin: new ConnectionLaunchOrigin(
                "connection-1", "workspace-1", "user-1", "conversation-1", "message-1"),
            SpawnOrigin: new AgentJobSpawnOrigin(
                "parent-session-1", "parent-agent-1", "edge-1", sessionId, jobId, turnId),
            WorkflowOrigin: new WorkflowAgentJobOrigin(
                "invocation-1", "command-1", "workflow-1", "attempt-1", "work-1", "build", "fingerprint-1")));

        var fact = new AgentSessionActivityConverged(
            sessionId,
            RunnerSessionActivityObservations.UnknownToRunner,
            ContextGeneration: 3,
            BindingEpoch: 7,
            SettledTurnIds: [turnId],
            SettledJobIds: [jobId],
            SupersededOperationIds: ["stop-original"],
            RecordedAt: _fixture.TimeProvider.GetUtcNow().UtcDateTime);
        var handler = new AgentSessionActivityConvergedHandler(Grains);
        var envelope = new CloudEvent<AgentSessionActivityConverged>(
            "activity-event-1",
            new Uri($"/mohist/agent-session/{sessionId}", UriKind.Relative),
            EventCatalog.ReverseDns.AgentSessionActivityConverged,
            _fixture.TimeProvider.GetUtcNow(),
            fact);

        await handler.HandleAsync(envelope, CancellationToken.None);
        await handler.HandleAsync(envelope, CancellationToken.None);
        await job.SubmitPreparedLaunchAsync();

        var snapshot = await job.GetRuntimeSnapshotAsync();
        var terminal = await job.GetTerminalResultAsync();
        Assert.Equal(AgentJobStatus.Unknown, snapshot.Status);
        Assert.False(snapshot.IsRecovering);
        Assert.Null(snapshot.RecoveryDeadlineAt);
        Assert.Equal(AgentJobStatus.Unknown, terminal.Status);

        var persisted = await ReadStateAsync(jobId);
        Assert.NotNull(persisted.TerminalAt);
        Assert.NotNull(persisted.ActivitySettlement);
        Assert.Equal(inputId, persisted.ActivitySettlement!.InitialInputId);
        Assert.Equal(turnId, persisted.ActivitySettlement.InitialTurnId);
        Assert.Equal(["stop-original"], persisted.ActivitySettlement.SupersededOperationIds);
        Assert.Null(persisted.PendingInitialTurnTerminalDelivery);
        Assert.Null(persisted.PendingSessionClose);
        Assert.Null(persisted.PendingWorkflowTerminalEvent);
        Assert.Null(persisted.PendingSubagentTerminalEvent);
        var ownerEvents = await _fixture.EventStore.ListAgentJobEventsAsync(jobId);
        Assert.Equal(3, ownerEvents.Count);
        Assert.Equal(
            [
                EventCatalog.ReverseDns.AgentJobTerminalDelivery,
                EventCatalog.ReverseDns.AgentJobWorkflowTerminal,
                EventCatalog.ReverseDns.AgentJobSubagentTerminal,
            ],
            ownerEvents.Select(item => item.Envelope.Type));
        Assert.All(ownerEvents, item =>
            Assert.Equal("unknown", item.Envelope.Data!.Value.GetProperty("status").GetString()));
        Assert.All(ownerEvents, item =>
            Assert.False(item.Envelope.Data!.Value.TryGetProperty("transcript", out _)));
        Assert.All(ownerEvents, item =>
            Assert.False(item.Envelope.Data!.Value.TryGetProperty("assistantReply", out _)));
    }

    [Theory]
    [InlineData("reminder")]
    [InlineData("persist")]
    public async Task FailedSettlementBoundary_ReloadsAuthoritativeStateAndRedeliverySucceeds(string fault)
    {
        var jobId = $"activity-retry-job-{fault}-{Guid.NewGuid():N}";
        var sessionId = $"activity-retry-session-{Guid.NewGuid():N}";
        var turnId = $"activity-retry-turn-{Guid.NewGuid():N}";
        var job = JobGrain(jobId);
        await job.PrepareManualLaunchAsync(new PrepareManualLaunchCommand(
            sessionId,
            $"activity-retry-input-{Guid.NewGuid():N}",
            turnId,
            "retry the settlement, not the work",
            ProjectId: "activity-project",
            AgentId: "activity-agent"));
        var command = new AgentJobActivityConvergence(
            sessionId,
            RunnerSessionActivityObservations.Idle,
            1,
            1,
            [turnId],
            [jobId],
            [],
            _fixture.TimeProvider.GetUtcNow());
        var failures = _fixture.Cluster.GetSiloServiceProvider(null)
            .GetRequiredService<ReportPersistenceFailureProbe>();
        if (fault == "reminder")
            failures.FailNextAgentJobActivitySettlementReminder(jobId);
        else
            failures.FailNextAgentJobActivitySettlementPersist(jobId);

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await job.ApplyActivityConvergenceAsync(command));
        var unchanged = await ReadStateAsync(jobId);
        Assert.Equal(AgentJobStatus.Pending, unchanged.Status);
        Assert.Null(unchanged.TerminalAt);
        Assert.Null(unchanged.ActivitySettlement);

        await job.AsReference<IGrainManagementExtension>().DeactivateOnIdle();
        var reloaded = JobGrain(jobId);
        Assert.True(await reloaded.ApplyActivityConvergenceAsync(command));
        var settled = await ReadStateAsync(jobId);
        Assert.Equal(AgentJobStatus.Unknown, settled.Status);
        Assert.NotNull(settled.TerminalAt);
        Assert.NotNull(settled.ActivitySettlement);
    }

    [Fact]
    public async Task UnrelatedOrStaleFact_CannotFinalizeOrOverwriteAJob()
    {
        var jobId = $"activity-unrelated-job-{Guid.NewGuid():N}";
        var sessionId = $"activity-unrelated-session-{Guid.NewGuid():N}";
        var turnId = $"activity-unrelated-turn-{Guid.NewGuid():N}";
        var job = JobGrain(jobId);
        await job.PrepareManualLaunchAsync(new PrepareManualLaunchCommand(
            sessionId,
            $"activity-unrelated-input-{Guid.NewGuid():N}",
            turnId,
            "retain the accepted identity",
            ProjectId: "activity-project",
            AgentId: "activity-agent"));

        var unrelated = new AgentJobActivityConvergence(
            "another-session",
            RunnerSessionActivityObservations.Idle,
            1,
            1,
            [turnId],
            [jobId],
            [],
            _fixture.TimeProvider.GetUtcNow());
        Assert.False(await job.ApplyActivityConvergenceAsync(unrelated));
        Assert.Equal(AgentJobStatus.Pending, await job.GetStatusAsync());

        var applicable = unrelated with
        {
            SessionId = sessionId,
            SettledTurnIds = [turnId],
        };
        Assert.True(await job.ApplyActivityConvergenceAsync(applicable));
        var original = (await ReadStateAsync(jobId)).ActivitySettlement!;

        var stale = applicable with
        {
            Observation = RunnerSessionActivityObservations.UnknownToRunner,
            ContextGeneration = 99,
            SupersededOperationIds = ["later-operation"],
        };
        Assert.False(await job.ApplyActivityConvergenceAsync(stale));
        var retained = (await ReadStateAsync(jobId)).ActivitySettlement!;
        Assert.Equal(original.Observation, retained.Observation);
        Assert.Equal(original.ContextGeneration, retained.ContextGeneration);
        Assert.Equal(original.SettledAt, retained.SettledAt);
        Assert.Empty(retained.SupersededOperationIds);

        var lateReport = await job.ReportResultAsync(
            "late-runner",
            "late-work",
            new Mohist.Server.Runner.Grains.WorkResult("failed", "late"));
        Assert.False(lateReport.Accepted);
        Assert.Equal(AgentJobStatus.Unknown, await job.GetStatusAsync());
    }

    private async Task<AgentJobState> ReadStateAsync(string jobId)
    {
        var factory = _fixture.Cluster.GetSiloServiceProvider(null)
            .GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var json = await db.AgentJobs.AsNoTracking()
            .Where(row => row.JobKey == jobId)
            .Select(row => row.State)
            .SingleAsync();
        return JsonSerializer.Deserialize<AgentJobState>(json, JSON.Options)!;
    }
}
