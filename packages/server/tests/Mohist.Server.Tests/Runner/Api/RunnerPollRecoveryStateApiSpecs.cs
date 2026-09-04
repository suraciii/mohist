using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.Tests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.Tests.Runner.Api;

[Collection("RunnerPollRecoveryStateApi")]
[Trait("level", "L1")]
public sealed class RunnerPollRecoveryStateApiSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public RunnerPollRecoveryStateApiSpecs(IsolatedMohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData(WorkDispatchOwnerKinds.Workflow, "success")]
    [InlineData(WorkDispatchOwnerKinds.Workflow, "ok")]
    [InlineData(WorkDispatchOwnerKinds.Workflow, "succeeded")]
    [InlineData(WorkDispatchOwnerKinds.Workflow, "arbitrary")]
    [InlineData(WorkDispatchOwnerKinds.AgentJob, "pass")]
    [InlineData(WorkDispatchOwnerKinds.AgentJob, "fail")]
    [InlineData(WorkDispatchOwnerKinds.AgentJob, "success")]
    [InlineData(WorkDispatchOwnerKinds.AgentJob, "ok")]
    public async Task ReportRoute_RejectsAliasesAndOwnerInvalidStatuses(string ownerKind, string status)
    {
        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/runner/status-validation-{Guid.NewGuid():N}/report",
            new
            {
                ownerKind,
                workflowRunId = ownerKind == WorkDispatchOwnerKinds.Workflow ? $"wr-{Guid.NewGuid():N}" : null,
                agentJobId = ownerKind == WorkDispatchOwnerKinds.AgentJob ? $"job-{Guid.NewGuid():N}" : null,
                workId = "work-1",
                status,
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }


    [Fact]
    public async Task ReportAndPoll_ExposeAcceptedContinuationIdentity()
    {
        var projectId = $"runner-recovery-{Guid.NewGuid():N}";
        var workflowRunId = $"wr-poll-recovery-{Guid.NewGuid():N}";
        var runnerId = $"runner-recovery-{Guid.NewGuid():N}";
        var recovery = new RecoveryDefinition(
            2,
            [new RecoveryHandlerDefinition("output.promise=FAIL", [], RetrySelf: true)]);
        var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);

        try
        {
            await SeedWorkflowAsync(projectId, workflowRunId, recovery);
            await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "test-host", projectId));

            var fresh = await PollAsync(runnerId);
            var freshWorkId = fresh.GetProperty("workId").GetString();
            var freshActionAttemptId = fresh.GetProperty("actionAttemptId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(freshWorkId));
            Assert.False(string.IsNullOrWhiteSpace(freshActionAttemptId));
            Assert.True(fresh.TryGetProperty("recoveryRemaining", out var freshState));
            Assert.Equal(JsonValueKind.Null, freshState.ValueKind);

            using var report = await _fixture.Client.PostAsJsonAsync($"/api/runner/{runnerId}/report", new
            {
                ownerKind = WorkDispatchOwnerKinds.Workflow,
                workflowRunId,
                workId = freshWorkId,
                actionAttemptId = freshActionAttemptId,
                status = "completed",
                addTasks = new[]
                {
                    new
                    {
                        id = "review",
                        title = "Review",
                        uses = "spec/review",
                        with = new { options = "${{ vars.agent }}" },
                        expect = new { markers = new[] { new { path = "review.md", failIf = "${{ vars.marker }}" } } },
                        recovery = new
                        {
                            budget = 2,
                            handlers = new[]
                            {
                                new { when = "output.promise=FAIL", tasks = Array.Empty<object>(), retrySelf = true },
                            },
                        },
                        recoveryRemaining = 1,
                    },
                },
            });
            Assert.Equal(HttpStatusCode.OK, report.StatusCode);
            var reportBody = await report.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("accepted", reportBody.GetProperty("verdict").GetString());

            var continuation = await PollAsync(runnerId);
            Assert.Equal(workflowRunId, continuation.GetProperty("workflowRunId").GetString());
            Assert.NotEqual(freshWorkId, continuation.GetProperty("workId").GetString());
            Assert.NotEqual(freshActionAttemptId, continuation.GetProperty("actionAttemptId").GetString());
            Assert.True(continuation.TryGetProperty("recoveryRemaining", out var continuationState));
            Assert.Equal(1, continuationState.GetInt32());
        }
        finally
        {
            await runner.UnregisterAsync();
        }
    }

    [Fact]
    public async Task Report_WorkflowPersistenceConflictReturnsOutstandingAndReplaySettles()
    {
        var projectId = $"workflow-outstanding-{Guid.NewGuid():N}";
        var workflowRunId = $"workflow-outstanding-run-{Guid.NewGuid():N}";
        var runnerId = $"workflow-outstanding-runner-{Guid.NewGuid():N}";
        var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);

        try
        {
            await SeedWorkflowAsync(projectId, workflowRunId, new RecoveryDefinition(1, []), uses: "spec/task");
            await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "test-host", projectId));
            var dispatch = await PollAsync(runnerId);
            var workId = dispatch.GetProperty("workId").GetString()!;
            var actionAttemptId = dispatch.GetProperty("actionAttemptId").GetString()!;
            var payload = new
            {
                ownerKind = WorkDispatchOwnerKinds.Workflow,
                workflowRunId,
                workId,
                actionAttemptId,
                status = "completed",
            };

            _fixture.ReportPersistenceFailures.FailNextWorkflowReport(workflowRunId, workId);
            using var outstanding = await _fixture.Client.PostAsJsonAsync($"/api/runner/{runnerId}/report", payload);
            Assert.Equal(HttpStatusCode.OK, outstanding.StatusCode);
            Assert.Equal("outstanding", (await outstanding.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("verdict").GetString());
            Assert.Equal("Running", await _fixture.Grains.GetGrain<IWorkflowGrain>(workflowRunId).GetRunStatusAsync());

            using var accepted = await _fixture.Client.PostAsJsonAsync($"/api/runner/{runnerId}/report", payload);
            Assert.Equal("accepted", (await accepted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("verdict").GetString());
            using var replay = await _fixture.Client.PostAsJsonAsync($"/api/runner/{runnerId}/report", payload);
            Assert.Equal("accepted", (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("verdict").GetString());
        }
        finally
        {
            await runner.UnregisterAsync();
        }
    }

    [Fact]
    public async Task Report_AgentJobLedgerConflictReturnsOutstandingAndReplaySettles()
    {
        var projectId = $"agent-job-outstanding-{Guid.NewGuid():N}";
        var runnerId = $"agent-job-outstanding-runner-{Guid.NewGuid():N}";
        var jobId = $"agent-job-outstanding-{Guid.NewGuid():N}";
        var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
        var job = _fixture.Grains.GetGrain<IAgentJobGrain>(jobId);
        var connectionId = $"{runnerId}-connection";
        var connectionTracker = _fixture.Services.GetRequiredService<RunnerConnectionTracker>();
        var connectionGeneration = connectionTracker.Register(runnerId, connectionId);

        try
        {
            var agentSessionId = $"session-{Guid.NewGuid():N}";
            var agentTurnId = $"turn-{Guid.NewGuid():N}";
            const string runtime = "opencode";
            var runtimeSessionId = $"runtime-{Guid.NewGuid():N}";
            await runner.RegisterAsync(
                new RunnerInfo(
                    runnerId,
                    ["spec/*"],
                    "test-host",
                    projectId,
                    RuntimeCatalogs: CapabilityCatalogTestHelpers.Create(),
                    ConnectionGeneration: connectionGeneration),
                TestRunnerGenerationExtensions.ProcessGeneration);
            await job.SubmitAsync(new AgentJobInput(
                "persist terminal report",
                WorkspacePath: "/tmp/agent-job-outstanding",
                ProjectId: projectId,
                Runtime: runtime,
                AgentId: "agent-test",
                AgentSessionId: agentSessionId,
                InitialTurnId: agentTurnId,
                PinnedRunnerId: runnerId));
            var dispatch = await PollAsync(
                runnerId,
                new RunnerPollRequest(
                    [],
                    [],
                    RuntimeReadiness: [new RuntimeReadinessWitness(runtime, Ready: true, Generation: 1)],
                    ConnectionId: connectionId,
                    ConnectionGeneration: connectionGeneration,
                    ProcessGeneration: TestRunnerGenerationExtensions.ProcessGeneration));
            var workId = dispatch.GetProperty("workId").GetString()!;
            var bindingRecorded = await job.RecordRuntimeSessionBindingAsync(
                runnerId, workId, agentSessionId, runtimeSessionId);
            Assert.True(bindingRecorded);
            Assert.Equal(AgentJobStatus.Running, await job.GetStatusAsync());
            var payload = new
            {
                ownerKind = WorkDispatchOwnerKinds.AgentJob,
                agentJobId = jobId,
                workId,
                status = "completed",
                agentSessionId,
                agentTurnId,
                runtime,
                runtimeSessionId,
            };

            _fixture.ReportPersistenceFailures.FailNextAgentJobReport(jobId, workId);
            using var outstanding = await _fixture.Client.PostAsJsonAsync($"/api/runner/{runnerId}/report", payload);
            Assert.Equal(HttpStatusCode.OK, outstanding.StatusCode);
            Assert.Equal("outstanding", (await outstanding.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("verdict").GetString());
            Assert.Equal(AgentJobStatus.Running, await job.GetStatusAsync());

            using var accepted = await _fixture.Client.PostAsJsonAsync($"/api/runner/{runnerId}/report", payload);
            Assert.Equal("accepted", (await accepted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("verdict").GetString());
            using var replay = await _fixture.Client.PostAsJsonAsync($"/api/runner/{runnerId}/report", payload);
            Assert.Equal("accepted", (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("verdict").GetString());
        }
        finally
        {
            connectionTracker.Unregister(runnerId, connectionId);
            await runner.UnregisterAsync();
        }
    }

    [Fact]
    public async Task Report_StaleWorkflowResultIsNotTracked()
    {
        using var report = await _fixture.Client.PostAsJsonAsync($"/api/runner/stale-{Guid.NewGuid():N}/report", new
        {
            ownerKind = WorkDispatchOwnerKinds.Workflow,
            workflowRunId = $"missing-report-{Guid.NewGuid():N}",
            workId = "task-1",
            status = "completed",
        });

        Assert.Equal(HttpStatusCode.OK, report.StatusCode);
        var body = await report.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("refused", body.GetProperty("verdict").GetString());
    }

    [Fact]
    public async Task Report_ExistingWorkflowStaleResultIsNotTracked()
    {
        var projectId = $"runner-stale-existing-{Guid.NewGuid():N}";
        var workflowRunId = $"wr-stale-existing-{Guid.NewGuid():N}";
        var runnerId = $"runner-stale-existing-{Guid.NewGuid():N}";
        var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);

        try
        {
            await SeedWorkflowAsync(
                projectId,
                workflowRunId,
                new RecoveryDefinition(1, []),
                uses: "spec/task");
            await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "test-host", projectId));

            var fresh = await PollAsync(runnerId);
            var workId = fresh.GetProperty("workId").GetString();
            var actionAttemptId = fresh.GetProperty("actionAttemptId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(workId));
            Assert.False(string.IsNullOrWhiteSpace(actionAttemptId));

            using var firstReport = await _fixture.Client.PostAsJsonAsync($"/api/runner/{runnerId}/report", new
            {
                ownerKind = WorkDispatchOwnerKinds.Workflow,
                workflowRunId,
                workId,
                actionAttemptId,
                status = "completed",
            });
            Assert.Equal(HttpStatusCode.OK, firstReport.StatusCode);
            var firstBody = await firstReport.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("accepted", firstBody.GetProperty("verdict").GetString());

            using var staleReport = await _fixture.Client.PostAsJsonAsync($"/api/runner/{runnerId}/report", new
            {
                ownerKind = WorkDispatchOwnerKinds.Workflow,
                workflowRunId,
                workId,
                actionAttemptId,
                status = "failed",
                message = "conflicting late result",
            });
            Assert.Equal(HttpStatusCode.OK, staleReport.StatusCode);
            var staleBody = await staleReport.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("refused", staleBody.GetProperty("verdict").GetString());
        }
        finally
        {
            await runner.UnregisterAsync();
        }
    }

    [Fact]
    public async Task Report_AcceptsStructuredActionOutput()
    {
        using var report = await _fixture.Client.PostAsJsonAsync($"/api/runner/report-output-{Guid.NewGuid():N}/report", new
        {
            ownerKind = WorkDispatchOwnerKinds.Workflow,
            workflowRunId = $"missing-report-output-{Guid.NewGuid():N}",
            workId = "task-1",
            status = "completed",
            output = new { kind = "action-result", status = "completed" },
        });

        Assert.Equal(HttpStatusCode.OK, report.StatusCode);
    }

    [Fact]
    public async Task Status_ReconcilesMissedStoppedPush()
    {
        var projectId = $"runner-status-recovery-{Guid.NewGuid():N}";
        var workflowRunId = $"wr-status-recovery-{Guid.NewGuid():N}";
        var runnerId = $"runner-status-recovery-{Guid.NewGuid():N}";
        var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);

        try
        {
            await SeedWorkflowAsync(projectId, workflowRunId, new RecoveryDefinition(
                1,
                [new RecoveryHandlerDefinition("output.promise=FAIL", [], RetrySelf: true)]));
            await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "test-host", projectId));
            await _fixture.Grains.GetGrain<IWorkflowGrain>(workflowRunId).StopAsync("missed push");

            using var response = await _fixture.Client.PostAsJsonAsync(
                $"/api/runner/{runnerId}/workflow-runs/status",
                new { workflowRunIds = new[] { workflowRunId } });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("Stopped", body.GetProperty("statuses").GetProperty(workflowRunId).GetString());
        }
        finally
        {
            await runner.UnregisterAsync();
        }
    }

    [Fact]
    public async Task Report_MalformedRecoveryFollowUpAcksAndFailsTheRunTerminally()
    {
        var projectId = $"runner-recovery-malformed-{Guid.NewGuid():N}";
        var workflowRunId = $"wr-poll-recovery-malformed-{Guid.NewGuid():N}";
        var runnerId = $"runner-recovery-malformed-{Guid.NewGuid():N}";
        var recovery = new RecoveryDefinition(
            2,
            [new RecoveryHandlerDefinition("output.promise=FAIL", [], RetrySelf: true)]);
        var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);

        try
        {
            await SeedWorkflowAsync(projectId, workflowRunId, recovery);
            await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "test-host", projectId));

            var fresh = await PollAsync(runnerId);

            // Follow-up carries a recovery declaration but omits the required
            // numeric recoveryRemaining. This is a permanent validation failure:
            // the server must ack (2xx) so the runner retires the work from
            // awaitingAck, and must fail the active task terminally rather than
            // leaving it running or throwing a non-2xx that the runner resends.
            using var report = await _fixture.Client.PostAsJsonAsync($"/api/runner/{runnerId}/report", new
            {
                ownerKind = WorkDispatchOwnerKinds.Workflow,
                workflowRunId,
                workId = fresh.GetProperty("workId").GetString(),
                actionAttemptId = fresh.GetProperty("actionAttemptId").GetString(),
                status = "completed",
                addTasks = new[]
                {
                    new
                    {
                        id = "review",
                        title = "Review",
                        uses = "spec/review",
                        recovery = new
                        {
                            budget = 2,
                            handlers = new[]
                            {
                                new { when = "output.promise=FAIL", tasks = Array.Empty<object>(), retrySelf = true },
                            },
                        },
                    },
                },
            });

            Assert.Equal(HttpStatusCode.OK, report.StatusCode);
            var reportBody = await report.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("accepted", reportBody.GetProperty("verdict").GetString());

            var workflow = _fixture.Grains.GetGrain<IWorkflowGrain>(workflowRunId);
            Assert.Equal("Failed", await workflow.GetRunStatusAsync());

            var status = await _fixture.Services
                .GetRequiredService<WorkflowQuerier>()
                .GetStatusAsync(workflowRunId);
            Assert.NotNull(status);
            Assert.NotNull(status!.Failure);
            Assert.Contains("Recovery follow-up rejected", status.Failure!.Message ?? string.Empty);
        }
        finally
        {
            await runner.UnregisterAsync();
        }
    }

    private async Task SeedWorkflowAsync(
        string projectId,
        string workflowRunId,
        RecoveryDefinition recovery,
        string uses = "spec/review")
    {
        var expect = new Dictionary<string, JsonElement?>
        {
            ["markers"] = JsonSerializer.SerializeToElement(new[]
            {
                new
                {
                    path = "review.md",
                    oneOf = new[] { "<promise>PASS</promise>", "<promise>FAIL</promise>" },
                    failIf = "<promise>FAIL</promise>",
                },
            }),
        };
        var definition = new WorkflowDefinition(
            [new StageDefinition(
                "check",
                [new TaskDefinition(
                    "review",
                    "Review",
                    uses,
                    Expect: uses == "spec/review" ? expect : null,
                    Recovery: uses == "spec/review" ? recovery : null)],
                [])]);
        const string templateId = "spec/recovery-poll";
        var factory = _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile
            {
                ProjectId = projectId,
                DefaultWorkflowProfileId = templateId,
            });
            db.WorkflowProfileRecords.Add(new WorkflowProfileRecordRow
            {
                ProjectId = projectId,
                ProfileId = templateId,
                Name = templateId,
                DefinitionSource = WorkflowYamlSerializer.ToYaml(definition),
            });
            db.ProjectWorkflowTemplates.Add(new ProjectWorkflowTemplateRow
            {
                ProjectId = projectId,
                TemplateId = templateId,
                Template = WorkflowGrainTestHelpers.SerializeProfile(definition, templateId),
            });
            await db.SaveChangesAsync();
        }

        var workflow = _fixture.Grains.GetGrain<IWorkflowGrain>(workflowRunId);
        await workflow.StartAsync(new WorkflowStartInput(Metadata: new(
            Name: null,
            CreatedAt: DateTimeOffset.UnixEpoch,
             ProjectId: projectId,
             IssueNumber: 1),
            VerificationCommand: "true"));
    }

    private async Task<JsonElement> PollAsync(string runnerId, RunnerPollRequest? request = null)
    {
        using var response = await _fixture.Client.PostRunnerPollAsync(runnerId, request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadFirstDispatchElementAsync()
            ?? throw new InvalidOperationException("Expected a dispatch from /poll");
    }
}
