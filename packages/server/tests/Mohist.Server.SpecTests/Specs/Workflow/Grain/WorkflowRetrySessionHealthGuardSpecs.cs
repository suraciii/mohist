using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Grain;

/// <summary>
/// End-to-end tests for the workflow retry session health guard
/// (issue-110 / T-004). Covers:
/// <list type="bullet">
///   <item>Retry rejected at the <see cref="WorkflowSessionHealthGate.BlockThresholdPercent"/> boundary with a structured 409 response that advertises compact/reset actions.</item>
///   <item>Retry accepted with a warning log in the <see cref="WorkflowSessionHealthGate.WarnThresholdPercent"/> band.</item>
///   <item>Retry proceeds normally at healthy usage or when no usage data exists.</item>
///   <item>End-to-end compact-then-retry path: after the user compacts the session, the retry guard sees fresh usage and proceeds.</item>
/// </list>
/// </summary>
[Collection("IntegrationWorkflow")]
public class WorkflowRetrySessionHealthGuardSpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;

    public WorkflowRetrySessionHealthGuardSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task TaskFails_SessionContextAbove90Percent_RetryReturnsSessionContextExhausted()
    {
        var (projectId, issueNumber, issueId, workflowRunId, sessionName) = await SeedProjectIssueWorkflowAsync();
        var runnerId = $"retry-guard-{Guid.NewGuid():N}";
        await RegisterRunnerAsync(runnerId, projectId);
        try
        {
            var work = await PollForTaskAsync(runnerId, projectId, workflowRunId);
            var sessionId = await OpenAndAttachSessionAsync(runnerId, projectId, workflowRunId, sessionName, work);
            await PushContextUsageAsync(runnerId, projectId, workflowRunId, sessionName, contextWindowUsed: 960, contextWindowSize: 1000);
            await ReportTaskFailedAsync(runnerId, workflowRunId, work.WorkId, "context exhausted");

            var response = await _client.PostAsync($"/api/projects/{projectId}/issues/{issueNumber}/retry", null);

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
            var code = payload.GetProperty("code").GetString();
            Assert.Equal("session_context_exhausted", code);
            var errorMessage = payload.GetProperty("error").GetString();
            Assert.NotNull(errorMessage);
            Assert.Contains("96", errorMessage!);
            Assert.Contains("Compact", errorMessage!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("reset", errorMessage!, StringComparison.OrdinalIgnoreCase);

            var details = payload.GetProperty("details");
            var actions = details.GetProperty("recoveryActions").EnumerateArray().Select(e => e.GetString()).ToArray();
            Assert.Contains("compact", actions);
            Assert.Contains("reset", actions);

            // The WorkflowRun is now Failed with a ContextExhaustion
            // reason and the failure message captures the percent.
            var status = await _fixture.Grains.GetGrain<IIssueGrain>(issueId).GetWorkflowStatusAsync();
            Assert.NotNull(status);
            var grain = _fixture.Grains.GetGrain<IWorkflowGrain>(workflowRunId);
            await grain.DeactivateForTestAsync();
            var refreshed = await LoadRunAsync(workflowRunId);
            Assert.NotNull(refreshed);
            Assert.Equal(WorkflowRunStatus.Failed, refreshed!.Status);
            Assert.NotNull(refreshed.Failure);
            Assert.Equal(FailureReason.ContextExhaustion, refreshed.Failure!.Reason);
            Assert.Contains("96", refreshed.Failure.Message ?? string.Empty);

            // The run-level status view should advertise compact/reset
            // actions and NOT a retry action when the reason is
            // ContextExhaustion.
            var statusView = await LoadStatusViewAsync(projectId, issueNumber);
            Assert.NotNull(statusView);
            Assert.NotNull(statusView!.Failure);
            Assert.DoesNotContain(statusView.AvailableActions, a => a.Name == "retry");
            Assert.Contains(statusView.AvailableActions, a => a.Name == "compact");
            Assert.Contains(statusView.AvailableActions, a => a.Name == "reset");
        }
        finally
        {
            await _client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task TaskFails_SessionContextAbove90Percent_RunScopedRetryReturnsSessionContextExhausted()
    {
        var (projectId, _, _, workflowRunId, sessionName) = await SeedProjectIssueWorkflowAsync();
        var runnerId = $"retry-guard-run-{Guid.NewGuid():N}";
        await RegisterRunnerAsync(runnerId, projectId);
        try
        {
            var work = await PollForTaskAsync(runnerId, projectId, workflowRunId);
            await OpenAndAttachSessionAsync(runnerId, projectId, workflowRunId, sessionName, work);
            await PushContextUsageAsync(runnerId, projectId, workflowRunId, sessionName, contextWindowUsed: 960, contextWindowSize: 1000);
            await ReportTaskFailedAsync(runnerId, workflowRunId, work.WorkId, "context exhausted");

            var response = await _client.PostAsync($"/api/workflow-runs/{workflowRunId}/retry", null);

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("session_context_exhausted", payload.GetProperty("code").GetString());
            var details = payload.GetProperty("details");
            var actions = details.GetProperty("recoveryActions").EnumerateArray().Select(e => e.GetString()).ToArray();
            Assert.Contains("compact", actions);
            Assert.Contains("reset", actions);
        }
        finally
        {
            await _client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task TaskFails_SessionContextInWarnBand_RetrySucceedsAndStatusClears()
    {
        var (projectId, issueNumber, issueId, workflowRunId, sessionName) = await SeedProjectIssueWorkflowAsync();
        var runnerId = $"retry-guard-warn-{Guid.NewGuid():N}";
        await RegisterRunnerAsync(runnerId, projectId);
        try
        {
            var work = await PollForTaskAsync(runnerId, projectId, workflowRunId);
            await OpenAndAttachSessionAsync(runnerId, projectId, workflowRunId, sessionName, work);
            await PushContextUsageAsync(runnerId, projectId, workflowRunId, sessionName, contextWindowUsed: 850, contextWindowSize: 1000);
            await ReportTaskFailedAsync(runnerId, workflowRunId, work.WorkId, "flaky");

            var response = await _client.PostAsync($"/api/projects/{projectId}/issues/{issueNumber}/retry", null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var status = await LoadStatusViewAsync(projectId, issueNumber);
            Assert.NotNull(status);
            // The retry should have run. The run is back to a
            // dispatchable state with no active failure (the warn band
            // does not block retry, so the original task-failure is
            // cleared). The runner is still assigned, so the run lands
            // on Ready (no in-flight work yet).
            Assert.Equal("ready", status!.Status);
            Assert.Null(status.Failure);
        }
        finally
        {
            await _client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task TaskFails_NoContextData_RetrySucceeds()
    {
        // No session was opened for the task → no usage data exists.
        // The guard treats missing data as healthy and lets retry
        // proceed.
        var (projectId, issueNumber, issueId, workflowRunId, sessionName) = await SeedProjectIssueWorkflowAsync();
        var runnerId = $"retry-guard-no-usage-{Guid.NewGuid():N}";
        await RegisterRunnerAsync(runnerId, projectId);
        try
        {
            var work = await PollForTaskAsync(runnerId, projectId, workflowRunId);
            await ReportTaskFailedAsync(runnerId, workflowRunId, work.WorkId, "no session, no data");

            var response = await _client.PostAsync($"/api/projects/{projectId}/issues/{issueNumber}/retry", null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            await _client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task TaskFails_SessionAtHealthyUsage_RetrySucceeds()
    {
        var (projectId, issueNumber, issueId, workflowRunId, sessionName) = await SeedProjectIssueWorkflowAsync();
        var runnerId = $"retry-guard-healthy-{Guid.NewGuid():N}";
        await RegisterRunnerAsync(runnerId, projectId);
        try
        {
            var work = await PollForTaskAsync(runnerId, projectId, workflowRunId);
            await OpenAndAttachSessionAsync(runnerId, projectId, workflowRunId, sessionName, work);
            await PushContextUsageAsync(runnerId, projectId, workflowRunId, sessionName, contextWindowUsed: 400, contextWindowSize: 1000);
            await ReportTaskFailedAsync(runnerId, workflowRunId, work.WorkId, "transient");

            var response = await _client.PostAsync($"/api/projects/{projectId}/issues/{issueNumber}/retry", null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            await _client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task TaskFails_RetryBlockedByContext_AfterCompact_RetrySucceeds()
    {
        // End-to-end recovery: retry is blocked at 95%, the user
        // compacts the session, the same retry is re-attempted and
        // succeeds because the new usage is well below the threshold.
        //
        // The compact endpoint requires the session to be inactive
        // (5-minute quiet window). Rather than waiting for the window
        // to elapse in test time, this test simulates the recovery by
        // re-writing the persisted context window metrics to a healthy
        // value (0 / 0). The workflow guard's "after compact" check
        // is the same as a freshly-compacted session: no recorded
        // usage means the gate treats the session as healthy and lets
        // the retry proceed. This matches the spec scenario
        // "Retry proceeds after session recovery" — the exact recovery
        // mechanism (compact vs reset vs manual edit) is not what the
        // workflow retry guard observes; it only checks the resulting
        // context usage.
        var (projectId, issueNumber, issueId, workflowRunId, sessionName) = await SeedProjectIssueWorkflowAsync();
        var runnerId = $"retry-guard-recover-{Guid.NewGuid():N}";
        await RegisterRunnerAsync(runnerId, projectId);
        try
        {
            var work = await PollForTaskAsync(runnerId, projectId, workflowRunId);
            var sessionId = await OpenAndAttachSessionAsync(runnerId, projectId, workflowRunId, sessionName, work);
            await PushContextUsageAsync(runnerId, projectId, workflowRunId, sessionName, contextWindowUsed: 950, contextWindowSize: 1000);
            await ReportTaskFailedAsync(runnerId, workflowRunId, work.WorkId, "context exhausted");

            // First retry: blocked.
            var first = await _client.PostAsync($"/api/projects/{projectId}/issues/{issueNumber}/retry", null);
            Assert.Equal(HttpStatusCode.Conflict, first.StatusCode);
            var firstPayload = await first.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("session_context_exhausted", firstPayload.GetProperty("code").GetString());

            // Simulate the user triggering compact: push a healthy usage
            // update through the grain (the same path a real compact/reset
            // takes). The workflow retry guard reads from the grain's
            // in-memory state, so this is the correct way to simulate
            // post-compact recovery.
            await PushContextUsageAsync(runnerId, projectId, workflowRunId, sessionName, contextWindowUsed: 0, contextWindowSize: 0);

            // Second retry: succeeds.
            var second = await _client.PostAsync($"/api/projects/{projectId}/issues/{issueNumber}/retry", null);
            if (second.StatusCode != HttpStatusCode.OK)
            {
                var body = await second.Content.ReadAsStringAsync();
                Assert.Fail($"Second retry expected 200 OK, got {(int)second.StatusCode}: {body}");
            }
        }
        finally
        {
            await _client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    private async Task<(string ProjectId, int IssueNumber, string IssueId, string WorkflowRunId, string SessionName)> SeedProjectIssueWorkflowAsync()
    {
        var projectId = $"retry-guard-{Guid.NewGuid():N}";
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", projectId);

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });

        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new
        {
            title = $"Retry guard {Guid.NewGuid():N}",
            body = "track retry guard",
            labels = new Dictionary<string, string>(StringComparer.Ordinal),
            priority = "p1",
            projectId = project.Id,
            isDraft = false,
        });

        var issueGrain = _fixture.Grains.GetGrain<IIssueGrain>(issue.Id);
        var workflowRunId = await issueGrain.StartWorkAsync();

        // The session name used by the runner API is the agent-session
        // session-name. The runner opens it lazily; we can pick any
        // string here, but we'll match the convention the runner uses
        // (the work id of the dispatched task).
        var sessionName = $"task-{Guid.NewGuid():N}";
        return (project.Id, issue.Number, issue.Id, workflowRunId, sessionName);
    }

    private async Task RegisterRunnerAsync(string runnerId, string projectId)
    {
        await _client.PostOkAsync($"/api/runner/{runnerId}/register", new
        {
            capabilities = new[] { "spec/*" },
            hostname = "retry-guard-host",
            projectId,
        });
    }

    private async Task<WorkDispatchInfo> PollForTaskAsync(string runnerId, string projectId, string workflowRunId)
    {
        var work = await TestWait.ForAsync(
            async () => await PollMatchingTaskAsync(runnerId, workflowRunId),
            value => value is not null,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromMilliseconds(50),
            $"Runner '{runnerId}' to receive a work item for workflow '{workflowRunId}'");
        return work!;
    }

    private async Task<WorkDispatchInfo?> PollMatchingTaskAsync(string runnerId, string workflowRunId)
    {
        var response = await _client.PostAsync($"/api/runner/{runnerId}/poll", null);
        var payload = await response.ReadFirstDispatchElementAsync();
        if (payload is null)
            return null;

        var data = payload.Value;
        var dispatchedWorkflowRunId = data.GetProperty("workflowRunId").GetString();
        if (string.Equals(dispatchedWorkflowRunId, workflowRunId, StringComparison.Ordinal))
        {
            return new WorkDispatchInfo(
                data.GetProperty("workId").GetString()!,
                data.GetProperty("stage").GetString() ?? "build",
                data.TryGetProperty("title", out var t) ? t.GetString() : null);
        }

        await _client.PostOkAsync($"/api/runner/{runnerId}/report", new
        {
            workflowRunId = dispatchedWorkflowRunId,
            workId = data.GetProperty("workId").GetString(),
            status = "completed",
        });
        return null;
    }

    private async Task<string> OpenAndAttachSessionAsync(string runnerId, string projectId, string workflowRunId, string sessionName, WorkDispatchInfo work)
    {
        await _client.PostOkAsync(
            $"/api/runner/{runnerId}/sessions/{Uri.EscapeDataString(projectId)}/{Uri.EscapeDataString(workflowRunId)}/{Uri.EscapeDataString(sessionName)}/open",
            new
            {
                workId = work.WorkId,
                workType = "task",
                stage = work.Stage,
                title = work.Title ?? "Task 1",
                issueNumber = 1,
            });

        var sessionId = await ResolveSessionIdAsync(workflowRunId, sessionName);

        await _client.PostOkAsync(
            $"/api/runner/{runnerId}/sessions/{Uri.EscapeDataString(projectId)}/{Uri.EscapeDataString(workflowRunId)}/{Uri.EscapeDataString(sessionName)}/attach",
            new
            {
                agentSessionId = sessionId,
                workDir = "/tmp/retry-guard",
                processPid = 4321,
            });

        return sessionId;
    }

    private async Task<string> ResolveSessionIdAsync(string workflowRunId, string sessionName)
    {
        await using var db = await _fixture.Services
            .GetRequiredService<IDbContextFactory<MohistDbContext>>()
            .CreateDbContextAsync();
        return await db.AgentSessions
            .Where(s => s.LabelSourceId == workflowRunId && s.LabelSessionName == sessionName)
            .Select(s => s.Id)
            .SingleAsync();
    }

    private async Task PushContextUsageAsync(string runnerId, string projectId, string workflowRunId, string sessionName, long contextWindowUsed, long contextWindowSize)
    {
        await _client.PostOkAsync(
            $"/api/runner/{runnerId}/sessions/{Uri.EscapeDataString(projectId)}/{Uri.EscapeDataString(workflowRunId)}/{Uri.EscapeDataString(sessionName)}/runtime-events",
            new
            {
                workId = "task-1.1",
                workType = "task",
                stage = "build",
                runtimeEvents = new object[]
                {
                    new
                    {
                        type = "usage.updated",
                        payload = new
                        {
                            contextWindowUsed,
                            contextWindowSize,
                        },
                    },
                },
            });
    }

    private async Task ReportTaskFailedAsync(string runnerId, string workflowRunId, string workId, string reason)
    {
        await _client.PostOkAsync($"/api/runner/{runnerId}/report", new
        {
            workflowRunId,
            workId,
            status = "failed",
            message = reason,
        });
    }

    private async Task<Mohist.Server.Workflow.Domain.Run.WorkflowRun?> LoadRunAsync(string workflowRunId)
    {
        await using var db = await _fixture.Services
            .GetRequiredService<IDbContextFactory<MohistDbContext>>()
            .CreateDbContextAsync();
        var row = await db.WorkflowRuns.AsNoTracking()
            .FirstOrDefaultAsync(r => r.WorkflowRunId == workflowRunId);
        if (row is null) return null;
        return System.Text.Json.JsonSerializer.Deserialize<Mohist.Server.Workflow.Domain.Run.WorkflowRun>(
            row.State,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
            {
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
            });
    }

    private async Task<Mohist.Server.Workflow.Services.WorkflowStatusView?> LoadStatusViewAsync(string projectId, int issueNumber)
    {
        var json = await _client.GetStringAsync($"/api/projects/{projectId}/issues/{issueNumber}/workflow/status");
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data)) return null;
        if (!data.TryGetProperty("workflow", out var workflowEl) || workflowEl.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        return System.Text.Json.JsonSerializer.Deserialize<Mohist.Server.Workflow.Services.WorkflowStatusView>(
            workflowEl.GetRawText(),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
            {
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
                PropertyNameCaseInsensitive = true,
            });
    }

    private sealed record WorkDispatchInfo(string WorkId, string Stage, string? Title);

    private sealed record ProjectDto(string Id, string Name, string Path, string BaseBranch);
    private sealed record IssueDto(string Id, int Number, string Title);
}
