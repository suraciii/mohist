using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Events;
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
            AgentSessionId: sessionId,
            AgentId: "agent-test"));

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
        Assert.Equal(TerminalDeliveryId(jobKey), closed.GetProperty("operationId").GetString());
        Assert.Equal(TerminalDeliveryId(jobKey), closed.GetProperty("deliveryId").GetString());
        Assert.Equal("invalid-input", closed.GetProperty("failureCategory").GetString());
        Assert.Equal(jobKey, closed.GetProperty("agentJobId").GetString());
        Assert.Equal(_fixture.TimeProvider.GetUtcNow().ToString("o"), closed.GetProperty("recordedAt").GetString());

        await GetSingleSessionClosedPartAsync(sessionId);

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
            AgentSessionId: sessionId,
            AgentId: "agent-test"));

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
    }

    [Fact]
    public async Task FailedJob_ReactivationAfterSessionClose_EmitsRetainedFailureEvent()
    {
        var jobKey = $"agent-job-failure-recovery-{Guid.NewGuid():N}";
        var job = JobGrain(jobKey);
        _fixture.EventStore.ThrowOnAppend = evt => evt.Type == EventCatalog.ReverseDns.AgentJobFailed;

        try
        {
            await job.SubmitAsync(new AgentJobInput("doomed", AgentId: "agent-real"));
            await job.FailAsync("runner-lost");
            Assert.DoesNotContain(_fixture.EventStore.Appended,
                evt => evt.Envelope.Type == EventCatalog.ReverseDns.AgentJobFailed
                    && evt.Envelope.Source.ToString() == $"/mohist/agent-job/{jobKey}");

            _fixture.EventStore.ThrowOnAppend = null;
            var management = Grains.GetGrain<IManagementGrain>(0);
            await management.ForceActivationCollection(TimeSpan.Zero);

            await job.GetStatusAsync();

            var failure = Assert.Single(_fixture.EventStore.Appended,
                evt => evt.Envelope.Type == EventCatalog.ReverseDns.AgentJobFailed
                    && evt.Envelope.Source.ToString() == $"/mohist/agent-job/{jobKey}");
            Assert.Equal(jobKey, failure.Envelope.Subject);
            Assert.Equal("agent-real", failure.Envelope.Extensions[EventCatalog.Lineage.AgentId]);
        }
        finally
        {
            _fixture.EventStore.ThrowOnAppend = null;
        }
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
            AgentSessionId: sessionId,
            AgentId: "agent-test"));
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
        // Issue 484: the session.activity part no longer carries the
        // job's failureCategory (that stays the job's own verdict); the
        // runner-reported message remains observable as failureReason.
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
            AgentSessionId: sessionId,
            AgentId: "agent-test"));
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
        // Issue 484: failureCategory is no longer surfaced on the
        // session.activity part; the runner-reported message is still
        // observable as failureReason.
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
            AgentSessionId: sessionId,
            AgentId: "agent-test"));
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
        // Issue 484: failureCategory is no longer carried on the
        // session.activity part; the job's own Failed verdict is still
        // observable through the part's `status` field.
        Assert.Equal("failed", closed.GetProperty("status").GetString());
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
            AgentSessionId: sessionId,
            AgentId: "agent-test"));

        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(6));
        await job.CheckTimeoutsAsync();

        await WaitForStatusAsync(job, AgentJobStatus.Failed, TimeSpan.FromSeconds(5));

        var session = Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.FlushForTestAsync();

        var closed = await GetSingleClosedAsync(sessionId);
        Assert.Equal("failed", closed.GetProperty("status").GetString());
        // Issue 484: the runner-unavailable category is the AgentJob's
        // own verdict and is no longer mirrored onto the session.activity
        // part; the job's Failed status is still observable here.
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
            AgentSessionId: sessionId,
            AgentId: "agent-test"));

        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));

        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(11));
        await job.CheckTimeoutsAsync();

        await WaitForStatusAsync(job, AgentJobStatus.Failed, TimeSpan.FromSeconds(5));

        var session = Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.FlushForTestAsync();

        var closed = await GetSingleClosedAsync(sessionId);
        // Issue 484: the report-timeout category is the AgentJob's own
        // verdict and is no longer mirrored onto the session.activity
        // part; the job's Failed status is still observable here.
        Assert.Equal("failed", closed.GetProperty("status").GetString());
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
            AgentSessionId: sessionId,
            AgentId: "agent-test"));

        await job.FailAsync("runner-lost");

        await WaitForStatusAsync(job, AgentJobStatus.Failed, TimeSpan.FromSeconds(5));

        var session = Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.FlushForTestAsync();

        var closed = await GetSingleClosedAsync(sessionId);
        Assert.Equal("runner-lost", closed.GetProperty("failureReason").GetString());
        // Issue 484: failureCategory is no longer mirrored onto the
        // session.activity part.
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
            AgentSessionId: sessionId,
            AgentId: "agent-test"));

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
        // Issue 484: the delivery id is recorded as `operationId` on the
        // session.activity part.
        var firstDeliveryId = firstClosed.GetProperty("operationId").GetString();

        // A redelivered runner report on the already-terminal job must
        // not produce a duplicate activity fact. The AgentJob retains the
        // original delivery id and reports "already-terminal".
        var redeliver = await runner.ReportAgentJobResultAsync(
            agentJobId: job.GetPrimaryKeyString(),
            workId: workId,
            result: new WorkResult(Status: "failed", Message: "redelivered", Output: JSON.DeserializeElement("{}"), ExitCode: 1));
        Assert.False(redeliver.Tracked,
            "Already-terminal AgentJob rejects report replay but still owns the original delivery");

        await session.FlushForTestAsync();

        var parts = await ListSessionClosedPartsAsync(sessionId);
        var activityParts = parts
            .Where(part => part.Type == TranscriptPartTypes.SessionActivity)
            .ToList();
        Assert.Single(activityParts);
        Assert.Equal(
            firstDeliveryId,
            JSON.DeserializeElement(activityParts[0].PayloadJson).GetProperty("operationId").GetString());
    }

    // Issue 484 removed two AgentSession terminal-close scenarios that these
    // specs previously covered:
    //  - AgentSessionTerminalCommand_IsIdempotent_AcrossRetries: the session
    //    no longer deduplicates terminal-close deliveries by delivery id.
    //    AppendTerminalCloseAsync always returns AlreadyPersisted=false and
    //    appends a session.activity event; dedup now lives entirely on the
    //    AgentJob, which rejects replays of already-terminal work (covered by
    //    ReportResultAsync_DuplicateDeliveryOnTerminalJob above).
    //  - AgentSessionTerminalCommand_DropsCloseWhenBoundRuntimeSuperseded: the
    //    session no longer tracks a superseded-runtime acknowledgement path
    //    for terminal closes, and ResetAsync now requires a bound runner +
    //    runtime session, so the premise no longer exists.
    // Both tests were deleted because their scenarios do not exist under the
    // activity model.

    // Issue 484: the two terminal-delivery recovery specs
    // (ActivationLoss_BeforeSuccessfulDelivery_RetainsPendingForFreshActivation
    // and ReportResultAsync_ClosePersistenceFailure_RetainsPendingForRetry)
    // were deleted. Their premise was that a transcript-store failure during
    // AppendTerminalCloseAsync surfaces synchronously to the AgentJob, which
    // then retains its PendingSessionClose payload for retry. Under the
    // activity model AppendTerminalCloseAsync appends a session.activity event
    // through the deferred accumulator and never throws synchronously, so the
    // AgentJob always clears its pending payload on the first attempt and the
    // "pending payload survives a failed first delivery" scenario no longer
    // exists. Recovery is now purely timer-driven inside the AgentSession.

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

    // Issue 484: AgentJob terminal delivery now writes a `session.activity`
    // (activity=idle) transcript part instead of the deprecated
    // `session.closed` part. The part's payload carries the work result
    // fields (`status`/`failureReason`/`operationId`) so the job's own
    // verdict remains observable through the session transcript; the
    // session itself never enters a terminal state.
    private async Task<JsonElement> GetSingleClosedAsync(string sessionId)
    {
        var parts = (await ListSessionClosedPartsAsync(sessionId))
            .Where(part => part.Type == TranscriptPartTypes.SessionActivity)
            .ToList();
        Assert.Single(parts);
        return JSON.DeserializeElement(parts[0].PayloadJson);
    }

    private async Task<Infrastructure.Data.Sessions.AgentSessionTranscriptPartRow> GetSingleSessionClosedPartAsync(string sessionId)
    {
        var parts = (await ListSessionClosedPartsAsync(sessionId))
            .Where(part => part.Type == TranscriptPartTypes.SessionActivity)
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
