using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Grain;

/// <summary>
/// Retry must proceed regardless of session context usage. Capacity is
/// OpenCode's responsibility (it auto-compacts); mohist does not gate
/// dispatch or retry on <c>contextUsagePercent</c> — it only responds to
/// real task failures. These specs lock that contract: a failed task
/// retries successfully whether the session is empty, healthy, or at
/// 100% context usage.
/// </summary>
public class WorkflowRetryIgnoresContextUsageSpecs : IClassFixture<IsolatedMohistIntegrationFixture>
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;

    private const string RuntimeSessionId = "runtime-retry-guard";

    public WorkflowRetryIgnoresContextUsageSpecs(IsolatedMohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    [Theory]
    [InlineData(0L, 0L, "no usage data")]
    [InlineData(400L, 1000L, "healthy usage")]
    [InlineData(850L, 1000L, "warn-range usage")]
    [InlineData(960L, 1000L, "near-capacity usage")]
    public async Task TaskFails_RetrySucceedsRegardlessOfContextUsage(long contextWindowUsed, long contextWindowSize, string label)
    {
        var (projectId, issueNumber, workflowRunId, sessionName) = await SeedProjectIssueWorkflowAsync();
        var runnerId = $"retry-{Guid.NewGuid():N}";
        await RegisterRunnerAsync(runnerId, projectId);
        try
        {
            var work = await PollForTaskAsync(runnerId, projectId, workflowRunId);
            var binding = await OpenAndAttachSessionAsync(
                runnerId,
                projectId,
                issueNumber,
                workflowRunId,
                sessionName,
                work);
            if (contextWindowSize > 0)
            {
                await PushContextUsageAsync(
                    runnerId,
                    projectId,
                    workflowRunId,
                    sessionName,
                    binding,
                    contextWindowUsed,
                    contextWindowSize);
            }
            await ReportTaskFailedAsync(runnerId, workflowRunId, work, $"failed at {label}");

            var response = await _client.PostAsync($"/api/projects/{projectId}/issues/{issueNumber}/retry", null);

            if (response.StatusCode != HttpStatusCode.OK)
            {
                var body = await response.Content.ReadAsStringAsync();
                Assert.Fail($"Retry at {label} expected 200 OK, got {(int)response.StatusCode}: {body}");
            }

            // The run is back to a dispatchable state with the failure cleared.
            var status = await LoadStatusViewAsync(projectId, issueNumber);
            Assert.NotNull(status);
            Assert.Null(status!.Failure);
            // Usage never blocks: no recovery gating actions are advertised.
            Assert.DoesNotContain(status.AvailableActions, a => a.Name == "compact");
            Assert.DoesNotContain(status.AvailableActions, a => a.Name == "reset");
            Assert.DoesNotContain(status.AvailableActions, a => a.Name == "start");
        }
        finally
        {
            await _client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Fact]
    public async Task TaskFails_NoSession_RetrySucceeds()
    {
        var (projectId, issueNumber, workflowRunId, sessionName) = await SeedProjectIssueWorkflowAsync();
        var runnerId = $"retry-no-session-{Guid.NewGuid():N}";
        await RegisterRunnerAsync(runnerId, projectId);
        try
        {
            var work = await PollForTaskAsync(runnerId, projectId, workflowRunId);
            await ReportTaskFailedAsync(runnerId, workflowRunId, work, "no session attached");

            var response = await _client.PostAsync($"/api/projects/{projectId}/issues/{issueNumber}/retry", null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            await _client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    private async Task<(string ProjectId, int IssueNumber, string WorkflowRunId, string SessionName)> SeedProjectIssueWorkflowAsync()
    {
        var projectId = $"retry-guard-{Guid.NewGuid():N}";
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", projectId);

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        await WorkflowGrainTestHelpers.SeedWorkflowTemplateAsync(
            _fixture.ConnectionString,
            $"retry-agent-{Guid.NewGuid():N}",
            new WorkflowDefinition([
                new StageDefinition(
                    "build",
                    [new TaskDefinition("task-1", "Task 1", "mohist/opencode")],
                    [])
            ]),
            project.Id);

        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new
        {
            title = $"Retry guard {Guid.NewGuid():N}",
            body = "track retry guard",
            labels = new Dictionary<string, string>(StringComparer.Ordinal),
            priority = "p1",
            projectId = project.Id,
            isDraft = false,
        });

        var issueGrain = _fixture.Grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(project.Id, issue.Number)));
        var workflowRunId = await issueGrain.StartWorkAsync();
        await _fixture.Grains.GetGrain<IEventDispatcherGrain>(EventDispatcherGrain.Global).DispatchNowAsync();

        var sessionName = $"task-{Guid.NewGuid():N}";
        return (project.Id, issue.Number, workflowRunId, sessionName);
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
        if (payload is null) return null;

        var data = payload.Value;
        var dispatchedWorkflowRunId = data.GetProperty("workflowRunId").GetString();
        if (string.Equals(dispatchedWorkflowRunId, workflowRunId, StringComparison.Ordinal))
        {
            return new WorkDispatchInfo(
                data.GetProperty("workId").GetString()!,
                data.GetProperty("taskRunId").GetString()!,
                data.GetProperty("stage").GetString() ?? "build",
                data.TryGetProperty("title", out var t) ? t.GetString() : null);
        }

        await _client.PostOkAsync($"/api/runner/{runnerId}/report", new
        {
            workflowRunId = dispatchedWorkflowRunId,
            workId = data.GetProperty("workId").GetString(),
            taskRunId = data.GetProperty("taskRunId").GetString(),
            status = "completed",
        });
        return null;
    }

    private async Task<WorkflowRuntimeBinding> OpenAndAttachSessionAsync(
        string runnerId,
        string projectId,
        int issueNumber,
        string workflowRunId,
        string sessionName,
        WorkDispatchInfo work)
    {
        await _client.PostOkAsync(
            $"/api/runner/{runnerId}/sessions/{Uri.EscapeDataString(projectId)}/{Uri.EscapeDataString(workflowRunId)}/{Uri.EscapeDataString(sessionName)}/open",
            new
            {
                workId = work.WorkId,
                workType = "task",
                stage = work.Stage,
                title = work.Title ?? "Task 1",
                issueNumber,
                runtime = "opencode",
            });

        var sessionId = await ResolveSessionIdAsync(workflowRunId, sessionName);

        await _client.PostOkAsync(
            $"/api/runner/{runnerId}/sessions/{Uri.EscapeDataString(projectId)}/{Uri.EscapeDataString(workflowRunId)}/{Uri.EscapeDataString(sessionName)}/attach",
            new
            {
                runtimeSessionId = RuntimeSessionId,
                runtime = "opencode",
                expectedRuntime = "opencode",
                expectedRuntimeSessionId = (string?)null,
                workDir = "/tmp/retry-guard",
                processPid = 4321,
            });

        var inputDeliveryId = $"delivery-{Guid.NewGuid():N}";
        var receipt = await _fixture.Grains
            .GetGrain<IAgentSessionGrain>(sessionId)
            .AcceptWorkflowInputAsync(new AcceptWorkflowAgentSessionInputCommand(
                inputDeliveryId,
                "execute workflow task",
                workflowRunId,
                work.TaskRunId,
                work.WorkId,
                runnerId,
                "opencode",
                RuntimeSessionId,
                "{\"text\":\"execute workflow task\"}"));
        Assert.True(receipt.WorkflowBindingAccepted);
        return new WorkflowRuntimeBinding(
            inputDeliveryId,
            receipt.AgentSessionId,
            receipt.AgentTurnId,
            work.TaskRunId,
            work.WorkId);
    }

    private Task<string> ResolveSessionIdAsync(string workflowRunId, string sessionName) =>
        WorkflowApiTestSupport.ResolveSessionIdAsync(_fixture.Services, workflowRunId, sessionName);

    private async Task PushContextUsageAsync(
        string runnerId,
        string projectId,
        string workflowRunId,
        string sessionName,
        WorkflowRuntimeBinding binding,
        long contextWindowUsed,
        long contextWindowSize)
    {
        await _client.PostOkAsync(
            $"/api/runner/{runnerId}/sessions/{Uri.EscapeDataString(projectId)}/{Uri.EscapeDataString(workflowRunId)}/{Uri.EscapeDataString(sessionName)}/runtime-events",
            new
            {
                runtimeSessionId = RuntimeSessionId,
                inputDeliveryId = binding.InputDeliveryId,
                agentSessionId = binding.AgentSessionId,
                agentTurnId = binding.AgentTurnId,
                taskRunId = binding.TaskRunId,
                workId = binding.WorkId,
                workType = "task",
                stage = "build",
                runtime = "opencode",
                runtimeEvents = new object[]
                {
                    new
                    {
                        type = "usage.updated",
                        payload = new
                        {
                            turnId = binding.AgentTurnId,
                            contextWindowUsed,
                            contextWindowSize,
                        },
                    },
                },
            });
    }

    private async Task ReportTaskFailedAsync(
        string runnerId,
        string workflowRunId,
        WorkDispatchInfo work,
        string reason)
    {
        await _client.PostOkAsync($"/api/runner/{runnerId}/report", new
        {
            workflowRunId,
            workId = work.WorkId,
            taskRunId = work.TaskRunId,
            status = "failed",
            message = reason,
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

    private sealed record WorkDispatchInfo(string WorkId, string TaskRunId, string Stage, string? Title);
    private sealed record WorkflowRuntimeBinding(
        string InputDeliveryId,
        string AgentSessionId,
        string AgentTurnId,
        string TaskRunId,
        string WorkId);

    private sealed record ProjectDto(string Id, string Name);
    private sealed record IssueDto(int Number, string Title);
}
