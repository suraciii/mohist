using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Runner.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
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
    public async Task Poll_PreservesExplicitNullAndNumericRecoveryState()
    {
        var projectId = $"runner-recovery-{Guid.NewGuid():N}";
        var workflowRunId = $"wr-poll-recovery-{Guid.NewGuid():N}";
        var runnerId = $"runner-recovery-{Guid.NewGuid():N}";
        var recovery = new RecoveryDefinition(
            2,
            [new RecoveryHandlerDefinition("promise=FAIL", [], RetrySelf: true)]);
        var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);

        try
        {
            await SeedWorkflowAsync(projectId, workflowRunId, recovery);
            await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "test-host", projectId));

            var fresh = await PollAsync(runnerId);
            Assert.True(fresh.TryGetProperty("recoveryRemaining", out var freshState));
            Assert.Equal(JsonValueKind.Null, freshState.ValueKind);

            using var report = await _fixture.Client.PostAsJsonAsync($"/api/runner/{runnerId}/report", new
            {
                workflowRunId,
                workId = fresh.GetProperty("workId").GetString(),
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
                                new { when = "promise=FAIL", tasks = Array.Empty<object>(), retrySelf = true },
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
            [new RecoveryHandlerDefinition("promise=FAIL", [], RetrySelf: true)]);
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
                                new { when = "promise=FAIL", tasks = Array.Empty<object>(), retrySelf = true },
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
        var definition = new WorkflowDefinition(
            "spec/recovery-poll",
            [new StageDefinition("check", [new TaskDefinition("review", "Review", "spec/review", Recovery: recovery)], [])]);
        var factory = _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile
            {
                ProjectId = projectId,
                DefaultTemplateId = definition.Id,
            });
            db.ProjectWorkflowTemplates.Add(new ProjectWorkflowTemplateRow
            {
                ProjectId = projectId,
                TemplateId = definition.Id,
                Template = JsonSerializer.Serialize(definition, WorkflowYamlSerializer.JsonOptions),
            });
            await db.SaveChangesAsync();
        }

        var workflow = _fixture.Grains.GetGrain<IWorkflowGrain>(workflowRunId);
        await workflow.StartAsync(new WorkflowStartInput(Metadata: new(
            Name: null,
            CreatedAt: DateTimeOffset.UnixEpoch,
            Annotations: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["projectId"] = projectId,
                ["issueId"] = $"issue-{workflowRunId}",
                ["issueNumber"] = "1",
            })));
    }

    private async Task<JsonElement> PollAsync(string runnerId)
    {
        using var response = await _fixture.Client.PostAsync($"/api/runner/{runnerId}/poll", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadFirstDispatchElementAsync()
            ?? throw new InvalidOperationException("Expected a dispatch from /poll");
    }
}
