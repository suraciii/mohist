using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Runner.Grains;
using Mohist.Server.TestSupport;
using Orleans;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Grain;

/// <summary>
/// Typed terminal transport coverage for workflow-originated AgentJobs
/// (issue 559, T-003 / design D5+D6). <c>EnterTerminalStateAsync</c> stages
/// the durable <see cref="PendingWorkflowTerminalDelivery"/> obligation only
/// for jobs carrying the <see cref="AgentJobWorkflowInvocation"/>
/// discriminator and emits it as a
/// <c>com.mohist.agent.job.workflow-terminal</c> CloudEvent with the stable
/// event id <c>workflow-terminal:{jobKey}</c> and a typed payload (invocation
/// identity, terminal facts, boundary completion evaluation, timestamp). The
/// <c>agent-job-recovery</c> reminder retries the append after a failure and
/// the obligation clears exactly once. Agent facts never ride the Workflow
/// task-report endpoint: the Workflow-run event source stays silent for the
/// agent terminal. The consuming finalizer handler lands in T-004.
/// </summary>
[Collection("AgentJobGrain")]
public sealed class AgentJobWorkflowTerminalDeliverySpecs : AgentJobGrainTestSupport
{
    private const string WorkflowTerminalEventType = "com.mohist.agent.job.workflow-terminal";

    public AgentJobWorkflowTerminalDeliverySpecs(AgentJobGrainFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task WorkflowOriginatedJobTerminal_EmitsTypedWorkflowTerminalEventWithStableIdentity()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync($"wf-terminal-runner-{Guid.NewGuid():N}");
        var jobKey = $"agent-job-wf-terminal-{Guid.NewGuid():N}";
        var (workflowRunId, taskRunId, workId, invocationId, sessionId, inputId, turnId) = LineageFor(jobKey);
        var job = await LaunchWorkflowOriginatedJobAsync(jobKey, projectId, workflowRunId, taskRunId);
        await ClaimPreparedAgentJobAsync(runnerId);
        var snapshot = await job.GetRuntimeSnapshotAsync();

        await job.ReportResultAsync(
            snapshot.RunnerId!,
            snapshot.CurrentWorkId!,
            new WorkResult(
                Status: "completed",
                Message: "AgentJob completed",
                Output: CompletedOutputWithSatisfiedExpectation(),
                ExitCode: 0,
                ArtifactUploadIds: ["upload-terminal-1"]));

        await WaitForStatusAsync(job, AgentJobStatus.Completed, TimeSpan.FromSeconds(5));
        var recorded = Assert.Single(_fixture.EventStore.Appended, evt =>
            evt.Envelope.Type == WorkflowTerminalEventType
            && evt.Envelope.Source.ToString() == $"/mohist/agent-job/{jobKey}");
        var envelope = recorded.Envelope;

        // Stable event identity: retried or duplicated appends resolve
        // against the same id without a second outcome-shaping append.
        Assert.Equal(AgentJobSessionDeliveryIds.WorkflowTerminalEventId(jobKey), envelope.Id);
        Assert.Equal($"workflow-terminal:{jobKey}", envelope.Id);
        Assert.Equal(jobKey, envelope.Subject);

        // Lineage extensions stamp the AgentJob producer conformance.
        Assert.Equal("agent-test", envelope.Extensions[EventCatalog.Lineage.AgentId]);
        Assert.Equal(projectId, envelope.Extensions[EventCatalog.Lineage.ProjectId]);
        Assert.Equal(workflowRunId, envelope.Extensions[EventCatalog.Lineage.WorkflowRunId]);

        var data = envelope.Data!.Value;
        // Full invocation identity.
        Assert.Equal(invocationId, data.GetProperty("invocationId").GetString());
        Assert.Equal(projectId, data.GetProperty("projectId").GetString());
        Assert.Equal(workflowRunId, data.GetProperty("workflowRunId").GetString());
        Assert.Equal(taskRunId, data.GetProperty("taskRunId").GetString());
        Assert.Equal(workId, data.GetProperty("workId").GetString());
        Assert.Equal(jobKey, data.GetProperty("jobId").GetString());
        Assert.Equal(sessionId, data.GetProperty("sessionId").GetString());
        Assert.Equal(inputId, data.GetProperty("inputId").GetString());
        Assert.Equal(turnId, data.GetProperty("turnId").GetString());

        // Terminal facts. Null facts are omitted by the shared serializer.
        Assert.Equal("completed", data.GetProperty("status").GetString());
        Assert.Equal("AgentJob completed", data.GetProperty("message").GetString());
        Assert.False(data.TryGetProperty("failureReason", out _));
        Assert.False(data.TryGetProperty("failureCategory", out _));
        Assert.Equal(0, data.GetProperty("exitCode").GetInt32());
        Assert.Equal("upload-terminal-1", data.GetProperty("artifactUploadIds")[0].GetString());
        var output = data.GetProperty("output");
        Assert.Equal("opencode", output.GetProperty("kind").GetString());
        Assert.Contains("<promise>done</promise>", output.GetProperty("text").GetString());

        // The boundary completion evaluation rides the terminal facts typed.
        var evaluation = data.GetProperty("evaluation");
        Assert.True(evaluation.GetProperty("satisfied").GetBoolean());
        Assert.Equal("<promise>done</promise>", evaluation.GetProperty("matched").GetString());
        Assert.Empty(evaluation.GetProperty("missingFiles").EnumerateArray());
        Assert.Empty(evaluation.GetProperty("missingMarkers").EnumerateArray());
        Assert.Empty(evaluation.GetProperty("failIfMatches").EnumerateArray());
        Assert.Equal(
            "Workflow completion requirements satisfied",
            evaluation.GetProperty("message").GetString());

        Assert.Equal(
            _fixture.TimeProvider.GetUtcNow(),
            data.GetProperty("recordedAt").GetDateTimeOffset());

        // The successful append cleared the obligation exactly once.
        Assert.Null((await LoadJobStateAsync(jobKey)).PendingWorkflowTerminalDelivery);
    }

    [Fact]
    public async Task DirectAndRoutedShapedLaunches_StageNoWorkflowTerminalDelivery()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync($"wf-terminal-none-runner-{Guid.NewGuid():N}");

        // A direct launch (no workflow context at all).
        var direct = JobGrain($"agent-job-wf-none-direct-{Guid.NewGuid():N}");
        await direct.SubmitAsync(MakeInput("direct launch", projectId));
        await ClaimPreparedAgentJobAsync(runnerId);
        var directSnapshot = await direct.GetRuntimeSnapshotAsync();
        await direct.ReportResultAsync(
            directSnapshot.RunnerId!,
            directSnapshot.CurrentWorkId!,
            new WorkResult("completed"));
        await WaitForStatusAsync(direct, AgentJobStatus.Completed, TimeSpan.FromSeconds(5));

        // A routed-shaped launch carries WorkflowRunId without the
        // discriminator — it must stage nothing extra either.
        var routedShaped = JobGrain($"agent-job-wf-none-routed-{Guid.NewGuid():N}");
        await routedShaped.SubmitAsync(new AgentJobInput(
            Prompt: "routed-shaped launch",
            WorkspacePath: "/tmp/agent-job-wf-none-routed",
            ProjectId: projectId,
            AgentId: "agent-test",
            WorkflowRunId: $"workflow-run-routed-{Guid.NewGuid():N}"));
        await WaitForStatusAsync(routedShaped, AgentJobStatus.Running, TimeSpan.FromSeconds(5));
        var routedSnapshot = await routedShaped.GetRuntimeSnapshotAsync();
        await routedShaped.ReportResultAsync(
            routedSnapshot.RunnerId!,
            routedSnapshot.CurrentWorkId!,
            new WorkResult("completed"));
        await WaitForStatusAsync(routedShaped, AgentJobStatus.Completed, TimeSpan.FromSeconds(5));

        Assert.DoesNotContain(_fixture.EventStore.Appended, evt =>
            evt.Envelope.Type == WorkflowTerminalEventType
            && (evt.Envelope.Source.ToString() == $"/mohist/agent-job/{direct.GetPrimaryKeyString()}"
                || evt.Envelope.Source.ToString() == $"/mohist/agent-job/{routedShaped.GetPrimaryKeyString()}"));
        Assert.Null((await LoadJobStateAsync(direct.GetPrimaryKeyString())).PendingWorkflowTerminalDelivery);
        Assert.Null((await LoadJobStateAsync(routedShaped.GetPrimaryKeyString())).PendingWorkflowTerminalDelivery);
    }

    [Fact]
    public async Task AppendFailure_RetainsObligation_RecoveryEmitsOnceAndClears()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync($"wf-terminal-retry-runner-{Guid.NewGuid():N}");
        var jobKey = $"agent-job-wf-terminal-retry-{Guid.NewGuid():N}";
        var job = await LaunchWorkflowOriginatedJobAsync(
            jobKey,
            projectId,
            $"workflow-run-{Guid.NewGuid():N}",
            $"task-run-{Guid.NewGuid():N}");
        await ClaimPreparedAgentJobAsync(runnerId);
        var snapshot = await job.GetRuntimeSnapshotAsync();

        _fixture.EventStore.ThrowOnAppend = evt =>
            evt.Type == WorkflowTerminalEventType
            && evt.Id == AgentJobSessionDeliveryIds.WorkflowTerminalEventId(jobKey);
        try
        {
            await job.ReportResultAsync(
                snapshot.RunnerId!,
                snapshot.CurrentWorkId!,
                new WorkResult(
                    Status: "completed",
                    Message: "AgentJob completed",
                    Output: CompletedOutputWithSatisfiedExpectation(),
                    ExitCode: 0));
            await WaitForStatusAsync(job, AgentJobStatus.Completed, TimeSpan.FromSeconds(5));

            // The simulated append failure retains the durable obligation.
            var retained = await WaitForAsync(
                () => LoadJobStateAsync(jobKey),
                state => state.PendingWorkflowTerminalDelivery is not null,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromMilliseconds(25),
                "terminal-delivery obligation retained after append failure");
            Assert.Equal(
                AgentJobSessionDeliveryIds.WorkflowTerminalEventId(jobKey),
                retained.PendingWorkflowTerminalDelivery!.EventId);
            Assert.DoesNotContain(_fixture.EventStore.Appended, evt =>
                evt.Envelope.Type == WorkflowTerminalEventType
                && evt.Envelope.Source.ToString() == $"/mohist/agent-job/{jobKey}");

            // The recovery path (activation loss then reactivation resumes
            // the retained obligation) retries until the append succeeds.
            _fixture.EventStore.ThrowOnAppend = null;
            await TestLifecycle.DeactivateAndWait(job, Grains);
            await job.GetStatusAsync();

            var recorded = Assert.Single(_fixture.EventStore.Appended, evt =>
                evt.Envelope.Type == WorkflowTerminalEventType
                && evt.Envelope.Source.ToString() == $"/mohist/agent-job/{jobKey}");
            Assert.Equal(AgentJobSessionDeliveryIds.WorkflowTerminalEventId(jobKey), recorded.Envelope.Id);
            Assert.Equal(
                retained.PendingWorkflowTerminalDelivery!.RecordedAt,
                recorded.Envelope.Data!.Value.GetProperty("recordedAt").GetDateTimeOffset());

            // The obligation cleared exactly once.
            Assert.Null((await LoadJobStateAsync(jobKey)).PendingWorkflowTerminalDelivery);
        }
        finally
        {
            _fixture.EventStore.ThrowOnAppend = null;
        }
    }

    [Fact]
    public async Task DuplicateTerminalDelivery_ResolvesAgainstTheSameEventIdentity()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync($"wf-terminal-dup-runner-{Guid.NewGuid():N}");
        var jobKey = $"agent-job-wf-terminal-dup-{Guid.NewGuid():N}";
        var job = await LaunchWorkflowOriginatedJobAsync(
            jobKey,
            projectId,
            $"workflow-run-{Guid.NewGuid():N}",
            $"task-run-{Guid.NewGuid():N}");
        await ClaimPreparedAgentJobAsync(runnerId);
        var snapshot = await job.GetRuntimeSnapshotAsync();

        await job.ReportResultAsync(
            snapshot.RunnerId!,
            snapshot.CurrentWorkId!,
            new WorkResult(
                Status: "completed",
                Message: "AgentJob completed",
                Output: CompletedOutputWithSatisfiedExpectation(),
                ExitCode: 0));
        await WaitForStatusAsync(job, AgentJobStatus.Completed, TimeSpan.FromSeconds(5));

        // A stale duplicate report and a late failure command both replay
        // against the terminal: no second outcome-shaping append happens.
        await job.ReportResultAsync(
            snapshot.RunnerId!,
            snapshot.CurrentWorkId!,
            new WorkResult(Status: "failed", Message: "late duplicate"));
        await job.FailAsync("late failure after terminal");

        var recorded = Assert.Single(_fixture.EventStore.Appended, evt =>
            evt.Envelope.Type == WorkflowTerminalEventType
            && evt.Envelope.Source.ToString() == $"/mohist/agent-job/{jobKey}");
        Assert.Equal(AgentJobSessionDeliveryIds.WorkflowTerminalEventId(jobKey), recorded.Envelope.Id);
        Assert.Equal("completed", recorded.Envelope.Data!.Value.GetProperty("status").GetString());
        Assert.Equal(AgentJobStatus.Completed, await job.GetStatusAsync());
        Assert.Null((await LoadJobStateAsync(jobKey)).PendingWorkflowTerminalDelivery);
    }

    [Fact]
    public async Task FailedWorkflowJobTerminal_CarriesFailureFactsAndUnsatisfiedEvaluation()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync($"wf-terminal-failed-runner-{Guid.NewGuid():N}");
        var jobKey = $"agent-job-wf-terminal-failed-{Guid.NewGuid():N}";
        var job = await LaunchWorkflowOriginatedJobAsync(
            jobKey,
            projectId,
            $"workflow-run-{Guid.NewGuid():N}",
            $"task-run-{Guid.NewGuid():N}");
        await ClaimPreparedAgentJobAsync(runnerId);
        var snapshot = await job.GetRuntimeSnapshotAsync();

        // The agent turn completed (so the runner evaluated the frozen
        // expect) but the workspace contract was unsatisfied: the AgentJob
        // terminal verdict stays Completed while the evaluation reports the
        // miss — the Workflow finalizer owns the task decision (D6).
        await job.ReportResultAsync(
            snapshot.RunnerId!,
            snapshot.CurrentWorkId!,
            new WorkResult(
                Status: "failed",
                Message: "the runtime exploded",
                Output: JSON.DeserializeElement("""
                {
                  "kind": "opencode",
                  "status": "failure",
                  "text": null,
                  "error": "the runtime exploded",
                  "expectation": {
                    "satisfied": false,
                    "matched": null,
                    "missingFiles": [{ "path": "/tmp/agent-job-ws/plans/report.md" }],
                    "missingMarkers": [{ "path": "_output", "contains": "<promise>done</promise>" }],
                    "failIfMatches": [],
                    "message": "Workflow completion requirements were not satisfied: missing required file: /tmp/agent-job-ws/plans/report.md"
                  }
                }
                """),
                ExitCode: 1,
                Error: new Mohist.Server.Workflow.Domain.Run.ExecutionError(
                    Code: "turn-failed",
                    Message: "the runtime exploded")));

        await WaitForStatusAsync(job, AgentJobStatus.Failed, TimeSpan.FromSeconds(5));
        var recorded = Assert.Single(_fixture.EventStore.Appended, evt =>
            evt.Envelope.Type == WorkflowTerminalEventType
            && evt.Envelope.Source.ToString() == $"/mohist/agent-job/{jobKey}");
        var data = recorded.Envelope.Data!.Value;

        Assert.Equal("failed", data.GetProperty("status").GetString());
        Assert.Equal("the runtime exploded", data.GetProperty("failureReason").GetString());
        Assert.Equal("turn-failed", data.GetProperty("failureCategory").GetString());
        Assert.Equal(1, data.GetProperty("exitCode").GetInt32());

        var evaluation = data.GetProperty("evaluation");
        Assert.False(evaluation.GetProperty("satisfied").GetBoolean());
        Assert.False(evaluation.TryGetProperty("matched", out _));
        Assert.Equal(
            "/tmp/agent-job-ws/plans/report.md",
            evaluation.GetProperty("missingFiles")[0].GetProperty("path").GetString());
        Assert.Equal(
            "_output",
            evaluation.GetProperty("missingMarkers")[0].GetProperty("path").GetString());
        Assert.Contains(
            "Workflow completion requirements were not satisfied",
            evaluation.GetProperty("message").GetString());
    }

    [Fact]
    public async Task UnknownTransition_DoesNotStageWorkflowTerminalDelivery()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync($"wf-terminal-unknown-runner-{Guid.NewGuid():N}");
        var jobKey = $"agent-job-wf-terminal-unknown-{Guid.NewGuid():N}";
        var job = await LaunchWorkflowOriginatedJobAsync(
            jobKey,
            projectId,
            $"workflow-run-{Guid.NewGuid():N}",
            $"task-run-{Guid.NewGuid():N}");
        await ClaimPreparedAgentJobAsync(runnerId);

        await job.MarkUnknownAsync("runner state was inconclusive");
        await WaitForAsync(
            () => job.GetStatusAsync(),
            status => status == AgentJobStatus.Unknown,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(25),
            "job reaches Unknown");

        // Unknown is not a terminal verdict: no workflow-terminal delivery
        // is staged until an authoritative terminal resolves the job.
        Assert.DoesNotContain(_fixture.EventStore.Appended, evt =>
            evt.Envelope.Type == WorkflowTerminalEventType
            && evt.Envelope.Source.ToString() == $"/mohist/agent-job/{jobKey}");
        Assert.Null((await LoadJobStateAsync(jobKey)).PendingWorkflowTerminalDelivery);
    }

    [Fact]
    public async Task WorkflowTerminal_RidesItsOwnTransport_NotTheWorkflowTaskReportChannel()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync($"wf-terminal-channel-runner-{Guid.NewGuid():N}");
        var jobKey = $"agent-job-wf-terminal-channel-{Guid.NewGuid():N}";
        var workflowRunId = $"workflow-run-{Guid.NewGuid():N}";
        var job = await LaunchWorkflowOriginatedJobAsync(
            jobKey,
            projectId,
            workflowRunId,
            $"task-run-{Guid.NewGuid():N}");
        await ClaimPreparedAgentJobAsync(runnerId);
        var snapshot = await job.GetRuntimeSnapshotAsync();

        await job.ReportResultAsync(
            snapshot.RunnerId!,
            snapshot.CurrentWorkId!,
            new WorkResult(
                Status: "completed",
                Message: "AgentJob completed",
                Output: CompletedOutputWithSatisfiedExpectation(),
                ExitCode: 0));
        await WaitForStatusAsync(job, AgentJobStatus.Completed, TimeSpan.FromSeconds(5));

        // The typed workflow-terminal event on the AgentJob source is the
        // Agent-to-Workflow transport; the Workflow run event source — the
        // channel the Workflow task-report endpoint writes — stays silent:
        // no Agent execution facts ride a Workflow task-report payload.
        Assert.Single(_fixture.EventStore.Appended, evt =>
            evt.Envelope.Type == WorkflowTerminalEventType
            && evt.Envelope.Source.ToString() == $"/mohist/agent-job/{jobKey}");
        Assert.DoesNotContain(_fixture.EventStore.Appended, evt =>
            evt.Envelope.Source.ToString() == $"/mohist/workflow-runs/{workflowRunId}");
    }

    private static JsonElement CompletedOutputWithSatisfiedExpectation() => JSON.DeserializeElement("""
        {
          "kind": "opencode",
          "status": "success",
          "runtimeSessionId": "ses-wf-terminal",
          "model": null,
          "variant": null,
          "text": "the plan is written <promise>done</promise>",
          "error": null,
          "diagnostics": [],
          "expectation": {
            "satisfied": true,
            "matched": "<promise>done</promise>",
            "missingFiles": [],
            "missingMarkers": [],
            "failIfMatches": [],
            "message": "Workflow completion requirements satisfied"
          }
        }
        """);

    private static (string WorkflowRunId, string TaskRunId, string WorkId, string InvocationId, string SessionId, string InputId, string TurnId)
        LineageFor(string jobKey) => (
            $"workflow-run-wf-terminal-{jobKey}",
            $"task-run-wf-terminal-{jobKey}",
            $"workflow-work-{jobKey}",
            $"workflow-agent-invocation-{jobKey}",
            $"agent-session-wf-{jobKey}",
            $"workflow-agent-input-{jobKey}",
            $"workflow-agent-turn-{jobKey}");

    /// <summary>
    /// Materializes a workflow-originated AgentJob through the manual-launch
    /// entry points with the workflow discriminator — the exact participant
    /// shape the handoff activation produces (PrepareJob + SubmitJob).
    /// </summary>
    private async Task<IAgentJobGrain> LaunchWorkflowOriginatedJobAsync(
        string jobKey,
        string projectId,
        string workflowRunId,
        string taskRunId,
        string? expect = "{\"files\":[\"plans/agent.md\"]}")
    {
        var job = JobGrain(jobKey);
        await job.PrepareManualLaunchAsync(new PrepareManualLaunchCommand(
            SessionId: $"agent-session-wf-{jobKey}",
            InputId: $"workflow-agent-input-{jobKey}",
            TurnId: $"workflow-agent-turn-{jobKey}",
            Prompt: "run the workflow agent task",
            ProjectId: projectId,
            AgentId: "agent-test",
            WorkflowRunId: workflowRunId,
            Skills: [],
            WorkflowInvocation: new AgentJobWorkflowInvocation(
                InvocationId: $"workflow-agent-invocation-{jobKey}",
                TaskRunId: taskRunId,
                WorkId: $"workflow-work-{jobKey}"),
            TimeoutMilliseconds: 60_000,
            Expect: expect));
        await job.SubmitPreparedLaunchAsync();
        return job;
    }

    private async Task<AgentJobState> LoadJobStateAsync(string jobKey)
    {
        await using var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IAgentJobStore>();
        var ledger = await jobs.LoadLedgerAsync(jobKey);
        Assert.NotNull(ledger);
        return JSON.Deserialize<AgentJobState>(ledger!.StateJson)!;
    }
}
