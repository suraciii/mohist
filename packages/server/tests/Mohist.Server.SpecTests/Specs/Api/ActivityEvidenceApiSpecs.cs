using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.SpecTests.Specs.Api;

[Collection("IntegrationApi")]
public class ActivityEvidenceApiSpecs : ProjectEventsApiTestSupport
{
    public ActivityEvidenceApiSpecs(MohistIntegrationFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task GetActivity_ReturnsRecordedAndSnapshotEvidenceWithExplicitScope()
    {
        var project = await CreateProjectAsync("activity-evidence");
        var workflowRunId = $"wf_{Guid.NewGuid():N}";
        var sessionId = $"session_{Guid.NewGuid():N}";
        var snapshotSessionId = $"session_snapshot_{Guid.NewGuid():N}";
        var runnerId = $"runner_{Guid.NewGuid():N}";
        await SeedIssueAsync(project.Id, 1);
        await SeedWorkflowRunAsync(project.Id, workflowRunId, 1);
        await SeedAgentSessionAsync(project.Id, sessionId);
        await SeedSnapshotSessionAsync(project.Id, snapshotSessionId);
        await AppendIssueEventAsync(project.Id, 1, "com.mohist.issue.created", FixedTime.AddMinutes(1));
        await AppendWorkflowEventAsync(workflowRunId, project.Id, 1, "com.mohist.workflow.stage.started", FixedTime.AddMinutes(2));
        await AppendAgentSessionEventAsync(sessionId, project.Id, "com.mohist.agent-session.runtime-bound", FixedTime.AddMinutes(3));
        await SeedWaitingIssueAsync(project.Id, 2, FixedTime.AddMinutes(4));
        await RegisterRunnerAsync(runnerId, project.Id, "activity-host", FixedTime.AddMinutes(5));

        await GetActivityAsync(project.Id, 200);
        var first = await GetActivityAsync(project.Id, 200);
        var second = await GetActivityAsync(project.Id, 200);

        Assert.Equal(first, second);
        Assert.Contains(first, entry => entry.Kind == "issue" && entry.Provenance == "recorded" && entry.Scope == "project" && entry.EventType == "com.mohist.issue.created");
        Assert.Contains(first, entry => entry.Kind == "workflow-run" && entry.Provenance == "recorded" && entry.WorkflowRunId == workflowRunId);
        var sessionEvent = Assert.Single(first, entry => entry.EventType == "com.mohist.agent-session.runtime-bound");
        Assert.Equal("agent-session", sessionEvent.Kind);
        Assert.Equal("recorded", sessionEvent.Provenance);
        Assert.Equal(sessionId, sessionEvent.SessionId);
        Assert.Contains(first, entry => entry.Kind == "agent-session" && entry.Provenance == "snapshot" && entry.SessionId == snapshotSessionId);
        Assert.Contains(first, entry => entry.Kind == "waiting" && entry.Provenance == "snapshot" && entry.IssueNumber == 2);
        Assert.Contains(first, entry => entry.Kind == "runner" && entry.Provenance == "snapshot" && entry.Scope == "global" && entry.RunnerId == runnerId);
        Assert.All(first, entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Id));
            Assert.False(string.IsNullOrWhiteSpace(entry.Title));
            Assert.False(string.IsNullOrWhiteSpace(entry.Description));
        });
        await _client.PostAsync($"/api/runner/{runnerId}/unregister", null);
    }

    [Fact]
    public async Task GetActivity_IsolatesProjectEvidenceWhileRepeatingRunnerOnlyAsGlobal()
    {
        var firstProject = await CreateProjectAsync("activity-first");
        var secondProject = await CreateProjectAsync("activity-second");
        var runnerId = $"runner_{Guid.NewGuid():N}";
        await SeedIssueAsync(firstProject.Id, 11);
        await SeedIssueAsync(secondProject.Id, 22);
        await AppendIssueEventAsync(firstProject.Id, 11, "first.project.event", FixedTime);
        await AppendIssueEventAsync(secondProject.Id, 22, "second.project.event", FixedTime);
        await RegisterRunnerAsync(runnerId, firstProject.Id, "shared-host", FixedTime);

        var first = await GetActivityAsync(firstProject.Id);
        var second = await GetActivityAsync(secondProject.Id);

        Assert.Contains(first, entry => entry.EventType == "first.project.event");
        Assert.DoesNotContain(first, entry => entry.EventType == "second.project.event");
        Assert.Contains(second, entry => entry.EventType == "second.project.event");
        Assert.DoesNotContain(second, entry => entry.EventType == "first.project.event");
        Assert.Contains(first, entry => entry.RunnerId == runnerId && entry.Scope == "global");
        Assert.Contains(second, entry => entry.RunnerId == runnerId && entry.Scope == "global");
        Assert.DoesNotContain(first.Concat(second), entry => entry.RunnerId == runnerId && entry.Scope == "project");
        await _client.PostAsync($"/api/runner/{runnerId}/unregister", null);
    }

    [Fact]
    public async Task GetActivity_AppliesLimitAfterStableMergedOrdering()
    {
        var project = await CreateProjectAsync("activity-limit");
        await SeedIssueAsync(project.Id, 1);
        await AppendIssueEventAsync(project.Id, 1, "older", FixedTime.AddMinutes(-1));
        await AppendIssueEventAsync(project.Id, 1, "newer", FixedTime.AddMinutes(1));

        var first = await GetActivityAsync(project.Id, 1);
        var second = await GetActivityAsync(project.Id, 1);

        Assert.Single(first);
        Assert.Equal(first, second);
        Assert.Equal("newer", first[0].EventType);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    public async Task GetActivity_RejectsLimitOutsideDeclaredRange(int limit)
    {
        var project = await CreateProjectAsync("activity-invalid-limit");

        using var response = await _client.GetAsync($"/api/projects/{project.Id}/activity?limit={limit}");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid_limit", body);
        Assert.Contains("between 1 and 200", body);
    }

    [Fact]
    public async Task GetActivity_DefaultLimitIsOneHundred()
    {
        var project = await CreateProjectAsync("activity-default-limit");
        await SeedIssueAsync(project.Id, 1);
        await SeedIssueEventHistoryAsync(project.Id, 1, 105);

        var result = await GetActivityAsync(project.Id);

        Assert.Equal(100, result.Count);
        Assert.Equal("history-105", result[0].Id);
    }

    private async Task SeedSnapshotSessionAsync(string projectId, string sessionId)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Mohist.Server.Sessions.Services.AgentSessionQueryMetadataKeys.ProjectId] = projectId,
            [Mohist.Server.Sessions.Services.AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
            [Mohist.Server.Sessions.Services.AgentSessionQueryMetadataKeys.IssueNumber] = "1",
            [GenericAgentSessionMetadata.AgentId] = "agent-activity",
            [GenericAgentSessionMetadata.AgentName] = "Activity Agent",
        };
        var session = new AgentSession
        {
            Id = sessionId,
            Runtime = new AgentSessionRuntime("activity-runner", null),
            Settings = new AgentSessionSettings("gpt-4o"),
            Status = new AgentSessionStatusSnapshot(AgentRuntimeSessionId: sessionId, CreatedAt: FixedTime.UtcDateTime),
            Metadata = new AgentSessionMetadata(labels),
        };
        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        db.AgentSessions.Add(new AgentSessionRow
        {
            Id = sessionId,
            AgentSessionId = sessionId,
            RunnerId = "activity-runner",
            State = JsonSerializer.Serialize(session, AgentSessionJson.JsonOptions),
            Status = "bound",
            CreatedAt = FixedTime.UtcDateTime,
        });
        await db.SaveChangesAsync();
    }

    private async Task RegisterRunnerAsync(string runnerId, string projectId, string hostname, DateTimeOffset registeredAt)
    {
        await _client.PostOkAsync($"/api/runner/{runnerId}/register", new
        {
            capabilities = new[] { "spec/*" },
            hostname,
            projectId,
            registeredAt,
        });
    }

    private Task<List<ActivityEntryResponse>> GetActivityAsync(string projectId, int? limit = null) =>
        _client.GetDataAsync<List<ActivityEntryResponse>>(
            $"/api/projects/{projectId}/activity{(limit is null ? string.Empty : $"?limit={limit}")}");

    private async Task SeedWaitingIssueAsync(string projectId, int number, DateTimeOffset requestedAt)
    {
        var workflowRunId = $"wf_{Guid.NewGuid():N}";
        var issue = new DomainIssue
        {
            ProjectId = projectId,
            Number = number,
            Title = "Awaiting review",
            Status = IssueStatus.InProgress,
        };
        issue.StartWorkflow(workflowRunId);
        var state = JsonSerializer.Serialize(new
        {
            Id = workflowRunId,
            Metadata = new { CreatedAt = FixedTime, Name = "test" },
            Status = "AwaitingApproval",
            CurrentStageId = "plan",
            Stages = new[]
            {
                new
                {
                    Id = "plan",
                    Attempt = 1,
                    RequiresApproval = true,
                    Status = "AwaitingApproval",
                    Tasks = new[]
                    {
                        new { Id = "proposal", DefinitionId = "proposal", Attempt = 1, Title = "Plan proposal", Status = "Completed", Uses = "mohist/opencode" },
                    },
                    Checks = new[]
                    {
                        new { Name = "plan-ok", Title = "Plan ok", Uses = "mohist/openspec-checks", Status = "Passed", Message = "ok" },
                    },
                    ApprovalStatus = new { Result = (string?)null, RequestedAt = requestedAt.ToString("O"), RespondedAt = (string?)null },
                },
            },
        });

        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        db.Issues.Add(new IssueRow { State = IssueStore.Serialize(issue) });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync(
            "INSERT OR REPLACE INTO WorkflowRuns (WorkflowRunId, State, ETag) VALUES ({0}, {1}, 0)",
            workflowRunId,
            state);
    }

    private sealed record ActivityEntryResponse(
        string Id,
        string Provenance,
        string Scope,
        string Kind,
        DateTimeOffset Time,
        string Title,
        string Description,
        string? EventType,
        int? IssueNumber,
        string? WorkflowRunId,
        string? SessionId,
        string? RunnerId,
        string? Status);
}
