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

[Collection("IntegrationRunner")]
public sealed class RunnerPollRecoveryStateApiSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public RunnerPollRecoveryStateApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Poll_PreservesCompletionContractAndRecoveryState()
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
            await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*", RunnerCapabilities.WorkflowTaskCompletionBoundaryV1], "test-host", projectId));

            var fresh = await PollAsync(runnerId);
            Assert.True(fresh.TryGetProperty("recoveryRemaining", out var freshState));
            Assert.Equal(JsonValueKind.Null, freshState.ValueKind);
            using var expectJson = JsonDocument.Parse(fresh.GetProperty("expect").GetString()!);
            var marker = expectJson.RootElement.GetProperty("markers")[0];
            Assert.Equal("review.md", marker.GetProperty("path").GetString());
            Assert.Equal("<promise>FAIL</promise>", marker.GetProperty("failIf").GetString());

            using var report = await _fixture.Client.PostAsJsonAsync($"/api/runner/{runnerId}/report", new
            {
                workflowRunId,
                workId = fresh.GetProperty("workId").GetString(),
                taskRunId = fresh.GetProperty("taskRunId").GetString(),
                status = "completed",
                completionBoundary = BuildCleanBoundary(workflowRunId, runnerId, fresh),
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

            var continuation = await PollAsync(runnerId);
            Assert.True(continuation.TryGetProperty("recoveryRemaining", out var continuationState));
            Assert.Equal(1, continuationState.GetInt32());
            using var continuationWith = JsonDocument.Parse(continuation.GetProperty("with").GetString()!);
            Assert.Equal("${{ vars.agent }}", continuationWith.RootElement.GetProperty("options").GetString());
            using var continuationExpect = JsonDocument.Parse(continuation.GetProperty("expect").GetString()!);
            Assert.Equal("${{ vars.marker }}", continuationExpect.RootElement.GetProperty("markers")[0].GetProperty("failIf").GetString());
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
            await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*", RunnerCapabilities.WorkflowTaskCompletionBoundaryV1], "test-host", projectId));

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
                completionBoundary = BuildCleanBoundary(workflowRunId, runnerId, fresh),
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

    private async Task SeedWorkflowAsync(string projectId, string workflowRunId, RecoveryDefinition recovery)
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
            [new StageDefinition("check", [new TaskDefinition("review", "Review", "spec/review", Expect: expect, Recovery: recovery)], [])]);
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
        await workflow.StartAsync(new WorkflowStartInput(
            Metadata: new(
                Name: null,
                CreatedAt: DateTimeOffset.UnixEpoch,
                ProjectId: projectId,
                IssueNumber: 1),
            Workspace: new WorkspaceIdentity(
                "/virtual/workspace",
                "main",
                WorkspaceId: $"workspace-{workflowRunId}",
                WorkspaceGeneration: JsonSerializer.SerializeToElement(1),
                Head: "head-1",
                Tree: "tree-1")));
    }

    private static WorkflowTaskCompletionBoundary BuildCleanBoundary(
        string workflowRunId,
        string runnerId,
        JsonElement dispatch)
    {
        var identity = new WorkflowTaskExecutionIdentity(
            workflowRunId,
            dispatch.GetProperty("stage").GetString(),
            dispatch.GetProperty("taskRunId").GetString()!,
            dispatch.GetProperty("workId").GetString()!,
            WorkDispatchOwnerKinds.Workflow,
            workflowRunId,
            runnerId,
            dispatch.GetProperty("workspaceId").GetString(),
            dispatch.GetProperty("workspaceGeneration").Clone());
        var receipt = new CommitReceipt(
            1,
            identity,
            "main",
            "head-1",
            "tree-1",
            "main",
            "head-1",
            "tree-1",
            [],
            [],
            [],
            true,
            null,
            DateTimeOffset.UnixEpoch);
        return new WorkflowTaskCompletionBoundary(
            1,
            identity,
            new ActionCompletion(1, true, "succeeded", "action", null, null, [], null, DateTimeOffset.UnixEpoch),
            receipt,
            WorkflowTaskWorkspaceOutcomes.CommittedClean,
            null,
            $"api-boundary:{workflowRunId}:{identity.TaskAttemptId}");
    }

    private async Task<JsonElement> PollAsync(string runnerId)
    {
        using var response = await _fixture.Client.PostAsync($"/api/runner/{runnerId}/poll", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadFirstDispatchElementAsync()
            ?? throw new InvalidOperationException("Expected a dispatch from /poll");
    }
}
