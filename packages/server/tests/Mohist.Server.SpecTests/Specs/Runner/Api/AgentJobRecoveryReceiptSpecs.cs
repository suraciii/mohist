using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Runner.Api;

/// <summary>
/// AgentJob-owned recovery receipts use the same durable receipt port as
/// Workflow work, but the AgentJob owner replaces its owner-ledger dispatch
/// identity instead of mutating a Workflow settlement.
/// </summary>
public sealed class AgentJobRecoveryReceiptSpecs : IClassFixture<RunnerConfigFixture>, IAsyncLifetime
{
    private readonly RunnerConfigFixture _fixture;

    public AgentJobRecoveryReceiptSpecs(RunnerConfigFixture fixture)
    {
        _fixture = fixture;
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => new(_fixture.UnregisterRunnersAsync());

    [Fact]
    public async Task MatchingReceiptCreatesFreshDispatchAndDuplicateIsIdempotent()
    {
        var setup = await CreateRunningJobAsync("resume");
        var operation = await FenceAsync(setup);
        var receipt = CreateInterruptedReceipt(setup, operation.OperationId, "job-receipt-1");

        using var firstResponse = await _fixture.Client.PostAsJsonAsync(
            $"/api/runner/{setup.RunnerId}/recovery-receipt",
            receipt);
        var firstText = await firstResponse.Content.ReadAsStringAsync();
        Assert.True(firstResponse.StatusCode == HttpStatusCode.OK, firstText);
        using var firstDocument = JsonDocument.Parse(firstText);
        var first = firstDocument.RootElement;
        Assert.True(
            string.Equals(
                RuntimeRecoveryReceiptAckStatuses.Accepted,
                first.GetProperty("status").GetString(),
                StringComparison.Ordinal),
            first.ToString());
        Assert.True(
            string.Equals("replacement-created", first.GetProperty("reason").GetString(), StringComparison.Ordinal),
            first.ToString());

        var snapshot = await setup.Job.GetRuntimeSnapshotAsync();
        Assert.Equal(AgentJobStatus.Pending, snapshot.Status);
        Assert.Equal(1, snapshot.RecoveryGeneration);
        Assert.NotEqual(setup.WorkId, snapshot.CurrentWorkId);
        Assert.Equal(setup.WorkId, snapshot.OriginalWorkId);

        var replacement = await setup.Runner.TryClaimAgentJobAsync(setup.JobId, setup.ProjectId);
        Assert.NotNull(replacement);
        Assert.NotEqual(setup.WorkId, replacement!.WorkId);
        Assert.Equal(AgentJobStatus.Running, await setup.Job.GetStatusAsync());

        using var duplicateResponse = await _fixture.Client.PostAsJsonAsync(
            $"/api/runner/{setup.RunnerId}/recovery-receipt",
            receipt);
        Assert.Equal(HttpStatusCode.OK, duplicateResponse.StatusCode);
        var duplicate = await duplicateResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(first.GetProperty("status").GetString(), duplicate.GetProperty("status").GetString());
        Assert.Equal(first.GetProperty("appliedReceiptId").GetString(), duplicate.GetProperty("appliedReceiptId").GetString());
        Assert.Equal(replacement.WorkId, (await setup.Job.GetRuntimeSnapshotAsync()).CurrentWorkId);
    }

    [Fact]
    public async Task NoFenceAndMismatchedReceiptAreRejectedWithoutChangingTheJob()
    {
        var setup = await CreateRunningJobAsync("reject");
        var noFence = CreateInterruptedReceipt(setup, "missing-operation", "job-receipt-no-fence");

        using var noFenceResponse = await _fixture.Client.PostAsJsonAsync(
            $"/api/runner/{setup.RunnerId}/recovery-receipt",
            noFence);
        Assert.Equal(HttpStatusCode.OK, noFenceResponse.StatusCode);
        var noFenceBody = await noFenceResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(RuntimeRecoveryReceiptAckStatuses.RejectedMismatch, noFenceBody.GetProperty("status").GetString());
        Assert.Equal("update-fence-mismatch", noFenceBody.GetProperty("reason").GetString());
        Assert.Equal(AgentJobStatus.Running, await setup.Job.GetStatusAsync());

        var operation = await FenceAsync(setup);
        var mismatch = CreateInterruptedReceipt(setup, operation.OperationId, "job-receipt-mismatch") with
        {
            WorkId = setup.WorkId + ".stale",
        };
        using var mismatchResponse = await _fixture.Client.PostAsJsonAsync(
            $"/api/runner/{setup.RunnerId}/recovery-receipt",
            mismatch);
        Assert.Equal(HttpStatusCode.OK, mismatchResponse.StatusCode);
        var mismatchBody = await mismatchResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(RuntimeRecoveryReceiptAckStatuses.RejectedMismatch, mismatchBody.GetProperty("status").GetString());
        Assert.Equal("binding-mismatch", mismatchBody.GetProperty("reason").GetString());
        Assert.Equal(AgentJobStatus.RecoverablyInterrupted, await setup.Job.GetStatusAsync());
    }

    [Fact]
    public async Task OriginalWorkIsNotRedeliveredAfterReplacementClaim()
    {
        var setup = await CreateRunningJobAsync("redelivery");
        var operation = await FenceAsync(setup);
        var receipt = CreateInterruptedReceipt(setup, operation.OperationId, "job-receipt-redelivery");

        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/runner/{setup.RunnerId}/recovery-receipt",
            receipt);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var replacement = await setup.Runner.TryClaimAgentJobAsync(setup.JobId, setup.ProjectId);
        Assert.NotNull(replacement);
        var dispatch = _fixture.Services
            .GetRequiredService<Mohist.Server.Runner.Services.DispatchService>();
        var poll = await dispatch.PollAsync(setup.RunnerId, new RunnerPollRequest([], []));

        var redelivery = Assert.Single(poll.Dispatches);
        Assert.Equal(replacement!.WorkId, redelivery.WorkId);
        Assert.NotEqual(setup.WorkId, redelivery.WorkId);
    }

    [Fact]
    public async Task MissingReceiptReachesExplicitInterruptedTerminalStateAtItsDeadline()
    {
        var setup = await CreateRunningJobAsync("receipt-deadline");
        await FenceAsync(setup);

        var marked = await setup.Job.GetRuntimeSnapshotAsync();
        var deadline = Assert.IsType<DateTimeOffset>(marked.UpdateInterruptionDeadlineAt);
        _fixture.TimeProvider.Advance(deadline - _fixture.TimeProvider.GetUtcNow());
        await setup.Job.ReceiveReminder("agent-job-recovery", default);

        Assert.Equal(AgentJobStatus.Interrupted, await setup.Job.GetStatusAsync());
        var terminal = await setup.Job.GetTerminalResultAsync();
        Assert.Equal(AgentJobStatus.Interrupted, terminal.Status);
        Assert.Equal("agent-result-unconfirmed", terminal.Message);
        Assert.Null(terminal.FailureReason);
        var final = await setup.Job.GetRuntimeSnapshotAsync();
        Assert.Equal(setup.WorkId, final.CurrentWorkId);
        Assert.Null(final.UpdateInterruptionDeadlineAt);
    }

    [Fact]
    public async Task CannotContinueUsesExplicitInterruptedTerminalStateAndPreservesIdentity()
    {
        var setup = await CreateRunningJobAsync("terminal");
        var operation = await FenceAsync(setup);

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IAgentJobStore>();
            var ledger = await store.LoadLedgerAsync(setup.JobId);
            Assert.NotNull(ledger);
            var state = JSON.Deserialize<AgentJobState>(ledger!.StateJson)!;
            state.LaunchVisibility = AgentLaunchVisibility.Rejected;
            await store.SaveLedgerAsync(ledger with
            {
                StateJson = JSON.Serialize(state),
                LaunchVisibility = "rejected",
            });
        }
        await TestLifecycle.Deactivate(setup.Job);

        var receipt = CreateInterruptedReceipt(setup, operation.OperationId, "job-receipt-terminal");
        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/runner/{setup.RunnerId}/recovery-receipt",
            receipt);
        var responseText = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, responseText);
        using var responseDocument = JsonDocument.Parse(responseText);
        var body = responseDocument.RootElement;
        Assert.True(
            string.Equals(
                RuntimeRecoveryReceiptAckStatuses.Accepted,
                body.GetProperty("status").GetString(),
                StringComparison.Ordinal),
            body.ToString());
        Assert.True(
            string.Equals("cannot-continue", body.GetProperty("reason").GetString(), StringComparison.Ordinal),
            body.ToString());

        var status = await setup.Job.GetStatusAsync();
        Assert.Equal(AgentJobStatus.Interrupted, status);
        var terminal = await setup.Job.GetTerminalResultAsync();
        Assert.Equal(AgentJobStatus.Interrupted, terminal.Status);
        Assert.Null(terminal.FailureReason);
        var snapshot = await setup.Job.GetRuntimeSnapshotAsync();
        Assert.Equal(setup.RunnerId, snapshot.RunnerId);
        Assert.Equal(setup.WorkId, snapshot.CurrentWorkId);

        using var duplicateResponse = await _fixture.Client.PostAsJsonAsync(
            $"/api/runner/{setup.RunnerId}/recovery-receipt",
            receipt);
        Assert.Equal(HttpStatusCode.OK, duplicateResponse.StatusCode);
        var duplicate = await duplicateResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(body.GetProperty("status").GetString(), duplicate.GetProperty("status").GetString());
    }

    private async Task<JobSetup> CreateRunningJobAsync(string name)
    {
        var projectId = $"agent-job-recovery-{name}-{Guid.NewGuid():N}";
        var runnerId = await _fixture.RegisterRunnerAsync(projectId, maxWorkflowSlots: 2);
        var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
        var jobId = $"agent-job-recovery-{name}-{Guid.NewGuid():N}";
        var sessionId = $"agent-session-recovery-{Guid.NewGuid():N}";
        var inputId = $"input-{Guid.NewGuid():N}";
        var turnId = $"turn-{Guid.NewGuid():N}";
        var runtimeSessionId = $"runtime-session-{Guid.NewGuid():N}";
        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);

        await session.OpenAsync(new OpenAgentSessionCommand(
            RunnerId: runnerId,
            AgentRuntime: "opencode",
            Metadata: new AgentSessionMetadata(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["mohist.io/project-id"] = projectId,
                ["mohist.io/source-kind"] = "agent-launch",
                ["mohist.io/agent-id"] = "agent-test",
            })));
        await session.EnsureInitialLaunchAsync(new EnsureInitialLaunchCommand(
            inputId,
            turnId,
            "resume this job",
            "agent-job",
            jobId,
            Runtime: "opencode"));

        var job = _fixture.Grains.GetGrain<IAgentJobGrain>(jobId);
        await job.SubmitAsync(new AgentJobInput(
            "resume this job",
            ProjectId: projectId,
            Runtime: "opencode",
            AgentId: "agent-test",
            AgentSessionId: sessionId,
            InitialInputId: inputId,
            InitialTurnId: turnId,
            PinnedRunnerId: runnerId));
        var claim = await runner.TryClaimAgentJobAsync(jobId, projectId);
        Assert.NotNull(claim);
        Assert.True(await job.RecordRuntimeSessionBindingAsync(
            runnerId,
            claim!.WorkId,
            sessionId,
            runtimeSessionId));

        return new JobSetup(projectId, runnerId, jobId, sessionId, inputId, turnId, runtimeSessionId, claim.WorkId, job, runner);
    }

    private async Task<RunnerUpdateOperation> FenceAsync(JobSetup setup)
    {
        var operation = new RunnerUpdateOperation(
            $"update-{Guid.NewGuid():N}",
            setup.RunnerId,
            _fixture.TimeProvider.GetUtcNow(),
            new List<RunnerUpdateWork>
            {
                new(
                    WorkDispatchOwnerKinds.AgentJob,
                    setup.JobId,
                    setup.WorkId,
                    null,
                    "agent-job"),
            });
        operation = await _fixture.Grains
            .GetGrain<IRunnerUpdateOperationGrain>(setup.RunnerId)
            .StartOrGetAsync(operation);
        Assert.True(await setup.Job.MarkUpdateInterruptedAsync(
            setup.RunnerId,
            setup.WorkId,
            operation.OperationId));
        operation = await _fixture.Grains
            .GetGrain<IRunnerUpdateOperationGrain>(setup.RunnerId)
            .MarkWorkAsync(
                operation.OperationId,
                WorkDispatchOwnerKinds.AgentJob,
                setup.JobId,
                setup.WorkId,
                null,
                RunnerUpdateWorkStatus.Marked);
        return operation;
    }

    private static RuntimeRecoveryReceipt CreateInterruptedReceipt(
        JobSetup setup,
        string operationId,
        string receiptId) =>
        new(
            WorkflowRunId: string.Empty,
            TaskRunId: string.Empty,
            WorkId: setup.WorkId,
            RunnerId: setup.RunnerId,
            AgentSessionId: setup.SessionId,
            AgentTurnId: setup.TurnId,
            Runtime: "opencode",
            RuntimeSessionId: setup.RuntimeSessionId,
            RecoveryGeneration: 0,
            ReceiptId: receiptId,
            Payload: new RuntimeRecoveryReceiptPayload(
                RuntimeRecoveryReceiptPayloadTypes.UpdateInterrupted,
                UpdateOperationId: operationId,
                StopConfirmed: true),
            OwnerKind: RuntimeRecoveryReceiptOwnerKinds.AgentJob,
            AgentJobId: setup.JobId);

    private sealed record JobSetup(
        string ProjectId,
        string RunnerId,
        string JobId,
        string SessionId,
        string InputId,
        string TurnId,
        string RuntimeSessionId,
        string WorkId,
        IAgentJobGrain Job,
        IRunnerGrain Runner);
}
