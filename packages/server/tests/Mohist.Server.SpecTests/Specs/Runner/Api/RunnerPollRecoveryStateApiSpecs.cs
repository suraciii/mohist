using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Runner.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Runner.Api;

public sealed class RunnerPollRecoveryStateApiSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public RunnerPollRecoveryStateApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
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
            var freshTaskRunId = fresh.GetProperty("taskRunId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(freshWorkId));
            Assert.False(string.IsNullOrWhiteSpace(freshTaskRunId));
            Assert.True(fresh.TryGetProperty("recoveryRemaining", out var freshState));
            Assert.Equal(JsonValueKind.Null, freshState.ValueKind);

            using var report = await _fixture.Client.PostAsJsonAsync($"/api/runner/{runnerId}/report", new
            {
                workflowRunId,
                workId = freshWorkId,
                taskRunId = freshTaskRunId,
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
            Assert.True(reportBody.GetProperty("tracked").GetBoolean());
            Assert.Equal("accepted", reportBody.GetProperty("reason").GetString());
            Assert.Equal(workflowRunId, reportBody.GetProperty("workflowRunId").GetString());

            var continuation = await PollAsync(runnerId);
            Assert.Equal(workflowRunId, continuation.GetProperty("workflowRunId").GetString());
            Assert.NotEqual(freshWorkId, continuation.GetProperty("workId").GetString());
            Assert.NotEqual(freshTaskRunId, continuation.GetProperty("taskRunId").GetString());
            Assert.True(continuation.TryGetProperty("recoveryRemaining", out var continuationState));
            Assert.Equal(1, continuationState.GetInt32());
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
                workflowRunId,
                workId = fresh.GetProperty("workId").GetString(),
                taskRunId = fresh.GetProperty("taskRunId").GetString(),
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
            Assert.True(reportBody.GetProperty("tracked").GetBoolean());
            Assert.Equal("accepted", reportBody.GetProperty("reason").GetString());

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

    [Fact]
    public async Task RecoveryReceipt_MalformedContractIsRejectedBeforeOwningGrainRouting()
    {
        var runnerId = $"runner-recovery-receipt-invalid-{Guid.NewGuid():N}";
        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/runner/{runnerId}/recovery-receipt",
            new { workflowRunId = $"missing-receipt-workflow-{Guid.NewGuid():N}" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_recovery_receipt", body.GetProperty("code").GetString());

        using var payloadConflict = await _fixture.Client.PostAsJsonAsync(
            $"/api/runner/{runnerId}/recovery-receipt",
            new
            {
                workflowRunId = "workflow-receipt-contract",
                taskRunId = "task-1",
                workId = "work-1",
                runnerId,
                agentSessionId = "session-1",
                agentTurnId = "turn-1",
                runtime = "opencode",
                runtimeSessionId = "runtime-session-1",
                recoveryGeneration = 0,
                receiptId = "receipt-contract-conflict",
                payload = new
                {
                    type = "update-interrupted",
                    updateOperationId = "update-1",
                    stopConfirmed = true,
                    result = new { status = "completed" },
                },
            });
        Assert.Equal(HttpStatusCode.BadRequest, payloadConflict.StatusCode);
    }

    [Fact]
    public async Task RecoveryReceipt_AppliesAndDeduplicatesTerminalResultThroughWorkflowRoute()
    {
        var projectId = $"runner-recovery-receipt-{Guid.NewGuid():N}";
        var workflowRunId = $"wf-recovery-receipt-{Guid.NewGuid():N}";
        var runnerId = $"runner-recovery-receipt-{Guid.NewGuid():N}";
        var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);

        try
        {
            await SeedWorkflowAsync(
                projectId,
                workflowRunId,
                new RecoveryDefinition(1, []),
                uses: "mohist/opencode");
            await runner.RegisterAsync(new RunnerInfo(runnerId, ["mohist/opencode"], "test-host", projectId));
            var fresh = await PollAsync(runnerId);
            Assert.Equal(workflowRunId, fresh.GetProperty("workflowRunId").GetString());
            Assert.True(fresh.TryGetProperty("taskRunId", out var taskRun));
            Assert.True(fresh.TryGetProperty("workId", out var work));
            var taskRunId = taskRun.GetString()!;
            var workId = work.GetString()!;
            Assert.False(string.IsNullOrWhiteSpace(taskRunId));
            Assert.False(string.IsNullOrWhiteSpace(workId));
            var binding = new AgentExecutionBinding(
                taskRunId,
                workId,
                runnerId,
                "route-receipt-session",
                "route-receipt-turn",
                "opencode",
                "route-receipt-runtime-session");
            var workflow = _fixture.Grains.GetGrain<IWorkflowGrain>(workflowRunId);
            Assert.Equal(ReportAck.Accepted, await workflow.BindAgentExecutionAsync(binding));

            var result = new WorkResult("completed", "route receipt");
            var receipt = new RuntimeRecoveryReceipt(
                workflowRunId,
                taskRunId,
                workId,
                runnerId,
                binding.AgentSessionId,
                binding.AgentTurnId,
                binding.Runtime,
                binding.RuntimeSessionId,
                0,
                "route-receipt-1",
                new RuntimeRecoveryReceiptPayload(
                    RuntimeRecoveryReceiptPayloadTypes.TerminalResult,
                    Result: result,
                    Fingerprint: RuntimeRecoveryReceiptFingerprint.For(result)));

            using var firstResponse = await _fixture.Client.PostAsJsonAsync(
                $"/api/runner/{runnerId}/recovery-receipt",
                receipt);
            Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
            var first = await firstResponse.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(RuntimeRecoveryReceiptAckStatuses.Accepted, first.GetProperty("status").GetString());
            Assert.Equal(receipt.ReceiptId, first.GetProperty("appliedReceiptId").GetString());

            using var duplicateResponse = await _fixture.Client.PostAsJsonAsync(
                $"/api/runner/{runnerId}/recovery-receipt",
                receipt);
            Assert.Equal(HttpStatusCode.OK, duplicateResponse.StatusCode);
            var duplicate = await duplicateResponse.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(first.GetProperty("status").GetString(), duplicate.GetProperty("status").GetString());
            Assert.Equal(first.GetProperty("appliedReceiptId").GetString(), duplicate.GetProperty("appliedReceiptId").GetString());
            Assert.Equal("Completed", await workflow.GetRunStatusAsync());
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
             IssueNumber: 1)));
    }

    private async Task<JsonElement> PollAsync(string runnerId)
    {
        using var response = await _fixture.Client.PostAsync($"/api/runner/{runnerId}/poll", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadFirstDispatchElementAsync()
            ?? throw new InvalidOperationException("Expected a dispatch from /poll");
    }
}
