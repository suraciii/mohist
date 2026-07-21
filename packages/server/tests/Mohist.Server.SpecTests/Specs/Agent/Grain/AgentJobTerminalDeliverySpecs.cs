using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Orleans;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Grain;

[Collection("AgentJobGrain")]
public class AgentJobTerminalDeliverySpecs : AgentJobGrainTestSupport
{
    public AgentJobTerminalDeliverySpecs(AgentJobGrainFixture fixture) : base(fixture)
    {
    }

    private static string TerminalDeliveryId(string jobKey) =>
        AgentJobSessionDeliveryIds.TerminalDeliveryId(jobKey);

    [Fact]
    public async Task ReportResultAsync_FailedRunnerReport_PersistsPendingCloseWithFailureReasonAndCategory()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync(
            $"agent-job-fail-runner-{Guid.NewGuid():N}");
        var jobKey = $"agent-job-fail-pending-{Guid.NewGuid():N}";
        var sessionId = $"session-fail-pending-{Guid.NewGuid():N}";
        await OpenSessionAsync(sessionId, projectId);

        var job = JobGrain(jobKey);
        await job.SubmitAsync(new AgentJobInput(
            Prompt: "do a failing thing",
            WorkspacePath: "/tmp/agent-job-fail-pending",
            ProjectId: projectId,
            AgentSessionId: sessionId));

        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));
        var snapshot = await job.GetRuntimeSnapshotAsync();
        var workId = snapshot.CurrentWorkId!;
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        await runner.ReportAgentJobResultAsync(
            jobKey,
            workId,
            new WorkResult(
                Status: "failed",
                Message: "AgentJob requires 'workspace.path' in dispatch variables",
                Output: JSON.DeserializeElement("{}"),
                ExitCode: 1,
                Error: new Mohist.Server.Workflow.Domain.Run.ExecutionError(
                    Code: "invalid-input",
                    Message: "missing workspace.path")));

        await WaitForStatusAsync(job, AgentJobStatus.Failed, TimeSpan.FromSeconds(5));

        var session = Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.FlushForTestAsync();

        var closed = await GetSingleClosedAsync(sessionId);
        Assert.Equal("failed", closed.GetProperty("status").GetString());
        Assert.Equal(
            "AgentJob requires 'workspace.path' in dispatch variables",
            closed.GetProperty("failureReason").GetString());
        Assert.Equal("invalid-input", closed.GetProperty("failureCategory").GetString());
        Assert.Equal(TerminalDeliveryId(jobKey), closed.GetProperty("deliveryId").GetString());
        Assert.Equal(jobKey, closed.GetProperty("agentJobId").GetString());

        var part = await GetSingleSessionClosedPartAsync(sessionId);
        Assert.Equal(TerminalDeliveryId(jobKey), part.CorrelationKey);
        Assert.Equal(TerminalDeliveryId(jobKey), part.CorrelationId);

        // A successful delivery clears the pending payload: the durable
        // state no longer owns a Session-close delivery obligation.
        var runtimeSnapshot = await job.GetRuntimeSnapshotAsync();
        Assert.False(runtimeSnapshot.HasPendingSessionClose,
            "Successful delivery clears the pending payload before the call returns");
    }

    [Fact]
    public async Task ReportResultAsync_SuccessfulRunnerReport_PersistsPendingCloseWithoutReasonOrCategory()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync(
            $"agent-job-success-pending-{Guid.NewGuid():N}");
        var jobKey = $"agent-job-success-pending-{Guid.NewGuid():N}";
        var sessionId = $"session-success-pending-{Guid.NewGuid():N}";
        await OpenSessionAsync(sessionId, projectId);

        var job = JobGrain(jobKey);
        await job.SubmitAsync(new AgentJobInput(
            Prompt: "do the thing",
            WorkspacePath: "/tmp/agent-job-success-pending",
            ProjectId: projectId,
            AgentSessionId: sessionId));

        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));
        var workId = (await job.GetRuntimeSnapshotAsync()).CurrentWorkId!;
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        await runner.ReportAgentJobResultAsync(
            jobKey,
            workId,
            new WorkResult(Status: "completed", Message: "ok", Output: JSON.DeserializeElement("{}"), ExitCode: 0));
        await WaitForStatusAsync(job, AgentJobStatus.Completed, TimeSpan.FromSeconds(5));

        var session = Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.FlushForTestAsync();

        var closed = await GetSingleClosedAsync(sessionId);
        Assert.Equal("completed", closed.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, closed.GetProperty("failureReason").ValueKind);
        Assert.Equal(JsonValueKind.Null, closed.GetProperty("failureCategory").ValueKind);
    }

    [Fact]
    public async Task ReportResultAsync_RunnerCategoryPrecedence_OutputJsonWinsOverErrorCodeAndStatus()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync(
            $"agent-job-cat-precedence-output-{Guid.NewGuid():N}");
        var jobKey = $"agent-job-cat-precedence-output-{Guid.NewGuid():N}";
        var sessionId = $"session-cat-precedence-output-{Guid.NewGuid():N}";
        await OpenSessionAsync(sessionId, projectId);

        var job = JobGrain(jobKey);
        await job.SubmitAsync(new AgentJobInput(
            Prompt: "do",
            WorkspacePath: "/tmp/agent-job-cat-precedence-output",
            ProjectId: projectId,
            AgentSessionId: sessionId));
        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));
        var workId = (await job.GetRuntimeSnapshotAsync()).CurrentWorkId!;
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        await runner.ReportAgentJobResultAsync(
            jobKey,
            workId,
            new WorkResult(
                Status: "failed",
                Message: "context exhausted",
                Output: JSON.DeserializeElement("""{"failureCategory":"context_exhausted"}"""),
                ExitCode: 1,
                Error: new Mohist.Server.Workflow.Domain.Run.ExecutionError(
                    Code: "runtime-failed",
                    Message: "runtime-failed")));
        await WaitForStatusAsync(job, AgentJobStatus.Failed, TimeSpan.FromSeconds(5));

        var session = Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.FlushForTestAsync();

        var closed = await GetSingleClosedAsync(sessionId);
        Assert.Equal("context_exhausted", closed.GetProperty("failureCategory").GetString());
        Assert.Equal("context exhausted", closed.GetProperty("failureReason").GetString());
    }

    [Fact]
    public async Task ReportResultAsync_RunnerCategoryPrecedence_ErrorCodeUsedWhenOutputCategoryMissing()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync(
            $"agent-job-cat-precedence-code-{Guid.NewGuid():N}");
        var jobKey = $"agent-job-cat-precedence-code-{Guid.NewGuid():N}";
        var sessionId = $"session-cat-precedence-code-{Guid.NewGuid():N}";
        await OpenSessionAsync(sessionId, projectId);

        var job = JobGrain(jobKey);
        await job.SubmitAsync(new AgentJobInput(
            Prompt: "do",
            WorkspacePath: "/tmp/agent-job-cat-precedence-code",
            ProjectId: projectId,
            AgentSessionId: sessionId));
        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));
        var workId = (await job.GetRuntimeSnapshotAsync()).CurrentWorkId!;
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        await runner.ReportAgentJobResultAsync(
            jobKey,
            workId,
            new WorkResult(
                Status: "failed",
                Message: "missing workspace",
                Output: JSON.DeserializeElement("{}"),
                ExitCode: 1,
                Error: new Mohist.Server.Workflow.Domain.Run.ExecutionError(
                    Code: "invalid-input",
                    Message: "missing workspace.path")));
        await WaitForStatusAsync(job, AgentJobStatus.Failed, TimeSpan.FromSeconds(5));

        var session = Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.FlushForTestAsync();

        var closed = await GetSingleClosedAsync(sessionId);
        Assert.Equal("invalid-input", closed.GetProperty("failureCategory").GetString());
        Assert.Equal("missing workspace", closed.GetProperty("failureReason").GetString());
    }

    [Fact]
    public async Task ReportResultAsync_RunnerCategoryPrecedence_StatusUsedWhenOutputAndCodeMissing()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync(
            $"agent-job-cat-precedence-status-{Guid.NewGuid():N}");
        var jobKey = $"agent-job-cat-precedence-status-{Guid.NewGuid():N}";
        var sessionId = $"session-cat-precedence-status-{Guid.NewGuid():N}";
        await OpenSessionAsync(sessionId, projectId);

        var job = JobGrain(jobKey);
        await job.SubmitAsync(new AgentJobInput(
            Prompt: "do",
            WorkspacePath: "/tmp/agent-job-cat-precedence-status",
            ProjectId: projectId,
            AgentSessionId: sessionId));
        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));
        var workId = (await job.GetRuntimeSnapshotAsync()).CurrentWorkId!;
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        await runner.ReportAgentJobResultAsync(
            jobKey,
            workId,
            new WorkResult(Status: "failed", Message: "boom", Output: JSON.DeserializeElement("{}"), ExitCode: 1));
        await WaitForStatusAsync(job, AgentJobStatus.Failed, TimeSpan.FromSeconds(5));

        var session = Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.FlushForTestAsync();

        var closed = await GetSingleClosedAsync(sessionId);
        Assert.Equal("failed", closed.GetProperty("failureCategory").GetString());
    }

    [Fact]
    public async Task DispatchExhaustion_PersistsCloseWithRunnerUnavailableCategory()
    {
        await ClearGlobalRunnerRegistryAsync();
        var projectId = $"agent-job-exhaust-{Guid.NewGuid():N}";
        var sessionId = $"session-exhaust-{Guid.NewGuid():N}";
        await OpenSessionAsync(sessionId, projectId);

        var job = JobGrain($"agent-job-exhaust-{Guid.NewGuid():N}");
        await job.SubmitAsync(new AgentJobInput(
            Prompt: "exhausted",
            WorkspacePath: "/tmp/agent-job-exhaust",
            ProjectId: projectId,
            AgentSessionId: sessionId));

        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(6));
        await job.CheckTimeoutsAsync();

        await WaitForStatusAsync(job, AgentJobStatus.Failed, TimeSpan.FromSeconds(5));

        var session = Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.FlushForTestAsync();

        var closed = await GetSingleClosedAsync(sessionId);
        Assert.Equal("failed", closed.GetProperty("status").GetString());
        Assert.Equal(AgentJobFailureReasons.RunnerUnavailable, closed.GetProperty("failureCategory").GetString());
    }

    [Fact]
    public async Task ReportTimeout_PersistsCloseWithReportTimeoutCategory()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync(
            $"agent-job-timeout-{Guid.NewGuid():N}");
        var sessionId = $"session-timeout-{Guid.NewGuid():N}";
        await OpenSessionAsync(sessionId, projectId);

        var job = JobGrain($"agent-job-timeout-{Guid.NewGuid():N}");
        await job.SubmitAsync(new AgentJobInput(
            Prompt: "never reports",
            WorkspacePath: "/tmp/agent-job-timeout",
            ProjectId: projectId,
            AgentSessionId: sessionId));

        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));

        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(11));
        await job.CheckTimeoutsAsync();

        await WaitForStatusAsync(job, AgentJobStatus.Failed, TimeSpan.FromSeconds(5));

        var session = Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.FlushForTestAsync();

        var closed = await GetSingleClosedAsync(sessionId);
        Assert.Equal(AgentJobFailureReasons.ReportTimeout, closed.GetProperty("failureCategory").GetString());
    }

    [Fact]
    public async Task ForcedFailAsync_PersistsCloseWithForcedReason()
    {
        var projectId = $"agent-job-forced-{Guid.NewGuid():N}";
        var sessionId = $"session-forced-{Guid.NewGuid():N}";
        await OpenSessionAsync(sessionId, projectId);

        var job = JobGrain($"agent-job-forced-{Guid.NewGuid():N}");
        await job.SubmitAsync(new AgentJobInput(
            Prompt: "doomed",
            WorkspacePath: "/tmp/agent-job-forced",
            ProjectId: projectId,
            AgentSessionId: sessionId));

        await job.FailAsync("runner-lost");

        await WaitForStatusAsync(job, AgentJobStatus.Failed, TimeSpan.FromSeconds(5));

        var session = Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.FlushForTestAsync();

        var closed = await GetSingleClosedAsync(sessionId);
        Assert.Equal("runner-lost", closed.GetProperty("failureReason").GetString());
        Assert.Equal("runner-lost", closed.GetProperty("failureCategory").GetString());
    }

    [Fact]
    public async Task ReportResultAsync_DuplicateDeliveryOnTerminalJob_RetainsOriginalDeliveryIdAndDeduplicatesCloseFact()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync(
            $"agent-job-dup-{Guid.NewGuid():N}");
        var sessionId = $"session-dup-{Guid.NewGuid():N}";
        await OpenSessionAsync(sessionId, projectId);

        var job = JobGrain($"agent-job-dup-{Guid.NewGuid():N}");
        await job.SubmitAsync(new AgentJobInput(
            Prompt: "do",
            WorkspacePath: "/tmp/agent-job-dup",
            ProjectId: projectId,
            AgentSessionId: sessionId));

        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));
        var workId = (await job.GetRuntimeSnapshotAsync()).CurrentWorkId!;
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        await runner.ReportAgentJobResultAsync(
            agentJobId: job.GetPrimaryKeyString(),
            workId: workId,
            result: new WorkResult(Status: "completed", Message: "first result", Output: JSON.DeserializeElement("{}"), ExitCode: 0));
        await WaitForStatusAsync(job, AgentJobStatus.Completed, TimeSpan.FromSeconds(5));

        var session = Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.FlushForTestAsync();

        var firstClosed = await GetSingleClosedAsync(sessionId);
        var firstDeliveryId = firstClosed.GetProperty("deliveryId").GetString();

        // A redelivered runner report on the already-terminal job must
        // not produce a duplicate close fact. The AgentJob retains the
        // original delivery id and reports "already-terminal".
        var redeliver = await runner.ReportAgentJobResultAsync(
            agentJobId: job.GetPrimaryKeyString(),
            workId: workId,
            result: new WorkResult(Status: "failed", Message: "redelivered", Output: JSON.DeserializeElement("{}"), ExitCode: 1));
        Assert.False(redeliver.Tracked,
            "Already-terminal AgentJob rejects report replay but still owns the original delivery");

        await session.FlushForTestAsync();

        var parts = await ListSessionClosedPartsAsync(sessionId);
        var closedParts = parts
            .Where(part => part.Type == TranscriptPartTypes.SessionClosed)
            .ToList();
        Assert.Single(closedParts);
        Assert.Equal(firstDeliveryId, closedParts[0].CorrelationKey);
    }

    [Fact]
    public async Task AgentSessionTerminalCommand_IsIdempotent_AcrossRetries()
    {
        var projectId = $"agent-job-idem-{Guid.NewGuid():N}";
        var sessionId = $"session-idem-{Guid.NewGuid():N}";
        await OpenSessionAsync(sessionId, projectId);

        var session = Grains.GetGrain<IAgentSessionGrain>(sessionId);
        var deliveryId = $"agent-job:some-key:terminal";
        var payload = JSON.Serialize(new Dictionary<string, object?>
        {
            ["status"] = "failed",
            ["failureReason"] = "first attempt",
            ["failureCategory"] = "invalid-input",
            ["agentJobId"] = "some-key",
            ["deliveryId"] = deliveryId,
            ["recordedAt"] = _fixture.TimeProvider.GetUtcNow().ToString("o"),
        });

        var first = await session.AppendTerminalCloseAsync(new AppendTerminalCloseCommand(
            SessionId: sessionId,
            DeliveryId: deliveryId,
            Status: "failed",
            ExitCode: 1,
            FailureReason: "first attempt",
            FailureCategory: "invalid-input",
            RecordedAt: _fixture.TimeProvider.GetUtcNow(),
            PayloadJson: payload));
        Assert.False(first.AlreadyPersisted);

        await session.FlushForTestAsync();
        var partsAfterFirst = (await ListSessionClosedPartsAsync(sessionId))
            .Where(p => p.Type == TranscriptPartTypes.SessionClosed)
            .Count();
        Assert.Equal(1, partsAfterFirst);

        var second = await session.AppendTerminalCloseAsync(new AppendTerminalCloseCommand(
            SessionId: sessionId,
            DeliveryId: deliveryId,
            Status: "failed",
            ExitCode: 1,
            FailureReason: "first attempt",
            FailureCategory: "invalid-input",
            RecordedAt: _fixture.TimeProvider.GetUtcNow(),
            PayloadJson: payload));
        Assert.True(second.AlreadyPersisted);

        await session.FlushForTestAsync();
        var partsAfterSecond = (await ListSessionClosedPartsAsync(sessionId))
            .Where(p => p.Type == TranscriptPartTypes.SessionClosed)
            .Count();
        Assert.Equal(1, partsAfterSecond);
    }

[Fact]
    public async Task AgentSessionTerminalCommand_DropsCloseWhenBoundRuntimeSuperseded()
    {
        var projectId = $"agent-job-supersede-{Guid.NewGuid():N}";
        var sessionId = $"session-supersede-{Guid.NewGuid():N}";
        await OpenSessionAsync(sessionId, projectId);
        var session = Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand("runtime-a"));
        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(10));
        await session.ResetAsync(new ResetAgentSessionCommand("runtime-a", "runtime-b"));
        await session.FlushForTestAsync();

        var deliveryId = $"agent-job:supersede:terminal";
        var recordedAt = _fixture.TimeProvider.GetUtcNow();
        var payload = JSON.Serialize(new Dictionary<string, object?>
        {
            ["status"] = "failed",
            ["failureReason"] = "AgentJob failure after runtime reset",
            ["failureCategory"] = "runner-unavailable",
            ["agentJobId"] = "supersede",
            ["deliveryId"] = deliveryId,
            ["recordedAt"] = recordedAt.ToString("o"),
        });
        var result = await session.AppendTerminalCloseAsync(new AppendTerminalCloseCommand(
            SessionId: sessionId,
            DeliveryId: deliveryId,
            Status: "failed",
            ExitCode: 1,
            FailureReason: "AgentJob failure after runtime reset",
            FailureCategory: "runner-unavailable",
            RecordedAt: recordedAt,
            PayloadJson: payload,
            RuntimeSessionId: "runtime-a"));

        Assert.True(result.AlreadyPersisted,
            "Superseded runtime closes acknowledge without persisting so the AgentJob clears its pending payload");

        await session.FlushForTestAsync();
        var parts = (await ListSessionClosedPartsAsync(sessionId))
            .Where(p => p.Type == TranscriptPartTypes.SessionClosed)
            .ToList();
        Assert.Empty(parts);
    }

    [Fact]
    public async Task ActivationLoss_BeforeSuccessfulDelivery_RetainsPendingForFreshActivation()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync(
            $"agent-job-activation-loss-{Guid.NewGuid():N}");
        var sessionId = $"session-activation-loss-{Guid.NewGuid():N}";
        await OpenSessionAsync(sessionId, projectId);

        var job = JobGrain($"agent-job-activation-loss-{Guid.NewGuid():N}");
        await job.SubmitAsync(new AgentJobInput(
            Prompt: "do",
            WorkspacePath: "/tmp/agent-job-activation-loss",
            ProjectId: projectId,
            AgentSessionId: sessionId));
        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));
        var workId = (await job.GetRuntimeSnapshotAsync()).CurrentWorkId!;
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        // Inject a transcript-persistence failure so the first delivery
        // attempt fails and the AgentJob retains the pending payload
        // even after the synchronous ReportResultAsync returns.
        _fixture.SessionPersistence.QueueFailures(1);
        await runner.ReportAgentJobResultAsync(
            agentJobId: job.GetPrimaryKeyString(),
            workId: workId,
            result: new WorkResult(Status: "failed", Message: "transient", Output: JSON.DeserializeElement("{}"), ExitCode: 1));
        await WaitForStatusAsync(job, AgentJobStatus.Failed, TimeSpan.FromSeconds(5));

        // The pending payload survives the failed first delivery — the
        // AgentJob caught the exception inside DeliverTerminalToSessionAsync
        // and left State.PendingSessionClose in place. This read happens
        // before any forced activation loss so the in-memory state is
        // the source of truth.
        var stillPending = await job.GetRuntimeSnapshotAsync();
        Assert.True(stillPending.HasPendingSessionClose,
            "AgentJob retains pending close after a failed first delivery");

        // Now allow success and force activation loss. The reactivated
        // grain runs OnActivateAsync, observes the persisted pending
        // payload, re-delivers, and converges on a single durable close
        // fact. The retry uses no wall-clock polling — only the durable
        // reminder + the next activation.
        _fixture.SessionPersistence.ResetFailures();
        await DeactivateGrainAsync(job);
        await job.GetRuntimeSnapshotAsync();
        await DeactivateGrainAsync(job);
        var after = await job.GetRuntimeSnapshotAsync();
        Assert.False(after.HasPendingSessionClose,
            "After successful redelivery the persistent state has no pending payload to repair");

        var session = Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.FlushForTestAsync();
        var parts = (await ListSessionClosedPartsAsync(sessionId))
            .Where(p => p.Type == TranscriptPartTypes.SessionClosed)
            .ToList();
        Assert.Single(parts);
        Assert.Equal(TerminalDeliveryId(job.GetPrimaryKeyString()), parts[0].CorrelationKey);
    }

    [Fact]
    public async Task ReportResultAsync_ClosePersistenceFailure_RetainsPendingForRetry()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync(
            $"agent-job-persist-failure-{Guid.NewGuid():N}");
        var sessionId = $"session-persist-failure-{Guid.NewGuid():N}";
        await OpenSessionAsync(sessionId, projectId);

        var job = JobGrain($"agent-job-persist-failure-{Guid.NewGuid():N}");
        await job.SubmitAsync(new AgentJobInput(
            Prompt: "do",
            WorkspacePath: "/tmp/agent-job-persist-failure",
            ProjectId: projectId,
            AgentSessionId: sessionId));
        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));
        var workId = (await job.GetRuntimeSnapshotAsync()).CurrentWorkId!;
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        // PersistFailure flag tells the AgentSession transcript store
        // to throw on the first save attempts, mimicking a transient
        // database failure. The AgentJob terminal delivery swallows the
        // exception inside DeliverTerminalToSessionAsync and keeps the
        // pending payload so a subsequent redelivery retries until
        // success.
        _fixture.SessionPersistence.QueueFailures(2);
        await runner.ReportAgentJobResultAsync(
            agentJobId: job.GetPrimaryKeyString(),
            workId: workId,
            result: new WorkResult(Status: "failed", Message: "boom", Output: JSON.DeserializeElement("{}"), ExitCode: 1));
        await WaitForStatusAsync(job, AgentJobStatus.Failed, TimeSpan.FromSeconds(5));

        // The failed first delivery leaves the pending payload durable
        // on the in-memory state — no deactivation was triggered (the
        // AgentJob catches the exception inside
        // DeliverTerminalToSessionAsync), so a synchronous snapshot
        // observes it before any redelivery.
        var stillPending = await job.GetRuntimeSnapshotAsync();
        Assert.True(stillPending.HasPendingSessionClose,
            "First delivery failure leaves pending payload durable on the in-memory state");

        // Force activation loss and re-deliver via OnActivateAsync. A
        // second queued failure keeps the repair delivery failing too,
        // so the durable reminder / pending payload must survive
        // activation churn without being silently cleared.
        await DeactivateGrainAsync(job);
        var afterFirstRepair = await job.GetRuntimeSnapshotAsync();
        Assert.True(afterFirstRepair.HasPendingSessionClose,
            "Activation-loss repair retries the same delivery; with persistence still failing the pending payload remains durable");

        // Allow the simulated failure to pass; the next redelivery
        // succeeds and converges on exactly one close fact.
        _fixture.SessionPersistence.ResetFailures();
        var redeliver = await runner.ReportAgentJobResultAsync(
            agentJobId: job.GetPrimaryKeyString(),
            workId: workId,
            result: new WorkResult(Status: "failed", Message: "boom", Output: JSON.DeserializeElement("{}"), ExitCode: 1));
        Assert.False(redeliver.Tracked);

        var session = Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.FlushForTestAsync();

        var parts = (await ListSessionClosedPartsAsync(sessionId))
            .Where(p => p.Type == TranscriptPartTypes.SessionClosed)
            .ToList();
        Assert.Single(parts);
        Assert.Equal(parts[0].CorrelationKey, TerminalDeliveryId(job.GetPrimaryKeyString()));

        // After the successful retry the pending payload is cleared
        // and remains cleared across a subsequent activation loss.
        await DeactivateGrainAsync(job);
        var finalSnapshot = await job.GetRuntimeSnapshotAsync();
        Assert.False(finalSnapshot.HasPendingSessionClose,
            "After successful redelivery the persistent state has no pending payload to repair");
    }

    private async Task OpenSessionAsync(string sessionId, string projectId)
    {
        var session = Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.OpenAsync(new OpenAgentSessionCommand(
            RunnerId: string.Empty,
            AgentRuntime: "opencode",
            WorkDir: "/tmp/agent-job-fixture",
            Metadata: new AgentSessionMetadata(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
                    [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
                    [GenericAgentSessionMetadata.AgentId] = "agent-test",
                })));
    }

    private async Task DeactivateGrainAsync(IGrain grain)
    {
        var mgmt = Grains.GetGrain<IManagementGrain>(0);
        await mgmt.ForceActivationCollection(TimeSpan.Zero);
    }

    private async Task<JsonElement> GetSingleClosedAsync(string sessionId)
    {
        var parts = (await ListSessionClosedPartsAsync(sessionId))
            .Where(part => part.Type == TranscriptPartTypes.SessionClosed)
            .ToList();
        Assert.Single(parts);
        return JSON.DeserializeElement(parts[0].PayloadJson);
    }

    private async Task<Infrastructure.Data.Sessions.AgentSessionTranscriptPartRow> GetSingleSessionClosedPartAsync(string sessionId)
    {
        var parts = (await ListSessionClosedPartsAsync(sessionId))
            .Where(part => part.Type == TranscriptPartTypes.SessionClosed)
            .ToList();
        Assert.Single(parts);
        return parts[0];
    }

    private async Task<List<Infrastructure.Data.Sessions.AgentSessionTranscriptPartRow>> ListSessionClosedPartsAsync(string sessionId)
    {
        await using var db = GrainTestConfig.CreateDbContext(_fixture.ConnectionString);
        var turnIds = await db.AgentSessionTranscriptTurns
            .Where(t => t.SessionId == sessionId)
            .Select(t => t.Id)
            .ToListAsync();
        if (turnIds.Count == 0) return [];
        return await db.AgentSessionTranscriptParts
            .Where(p => turnIds.Contains(p.TurnId))
            .OrderBy(p => p.Sequence)
            .ThenBy(p => p.Id)
            .ToListAsync();
    }
}
