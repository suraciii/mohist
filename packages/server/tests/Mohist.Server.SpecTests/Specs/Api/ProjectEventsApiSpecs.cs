using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Migrations;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.SpecTests.Specs.Api;

/// <summary>
/// Specs for issue-402 T-000 — the project-scoped event read endpoint
/// (<c>GET /api/projects/&#123;projectRef&#125;/events</c>) that powers the
/// Activity evidence view. The endpoint queries already-recorded events
/// from <c>IssueEvents</c>, <c>WorkflowRunEvents</c>, and
/// <c>AgentSessionEvents</c>, plus persisted session lifecycle facts, without
/// changing how events are recorded, emitted, or subscribed. These specs
/// verify cross-aggregate retrieval, time ordering, project scoping, and the
/// read-only contract.
/// </summary>
[Collection("IntegrationApi")]
public class ProjectEventsApiSpecs
{
    private static readonly DateTimeOffset FixedTime = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;

    public ProjectEventsApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GetProjectEvents_ReturnsEventsAcrossAllAggregates_TimeSorted()
    {
        var project = await CreateProjectAsync();
        var workflowRunId = $"wf_{Guid.NewGuid():N}";
        var sessionId = $"agent_session_{Guid.NewGuid():N}";

        await SeedIssueAsync(project.Id, number: 1);
        await SeedWorkflowRunAsync(project.Id, workflowRunId, issueNumber: 1);
        await SeedAgentSessionAsync(project.Id, sessionId);
        await SeedEpicAsync(project.Id, number: 1);

        var t0 = FixedTime.AddMinutes(-10);
        await AppendIssueEventAsync(project.Id, 1, "com.mohist.issue.created",
            time: t0, subject: "1");
        await AppendWorkflowEventAsync(workflowRunId, project.Id, 1,
            "com.mohist.workflow.stage.started",
            time: t0.AddMinutes(1), subject: null);
        await AppendAgentSessionEventAsync(sessionId, project.Id,
            "com.mohist.agent-session.runtime-bound",
            time: t0.AddMinutes(2), subject: sessionId);
        await AppendEpicEventAsync(project.Id, 1, "com.mohist.epic.created",
            time: t0.AddMinutes(3), subject: "1");

        var response = await _client.GetDataAsync<List<ProjectEventResponseDto>>(
            $"/api/projects/{project.Id}/events");

        Assert.Equal(4, response.Count);

        var byType = response.ToDictionary(e => e.Type);
        Assert.True(byType.ContainsKey("com.mohist.issue.created"));
        Assert.True(byType.ContainsKey("com.mohist.workflow.stage.started"));
        Assert.True(byType.ContainsKey("com.mohist.agent-session.runtime-bound"));
        Assert.True(byType.ContainsKey("coder_session_started"));

        var issueEntry = byType["com.mohist.issue.created"];
        Assert.Equal("issue", issueEntry.Origin);
        Assert.Equal("issue", issueEntry.SourceAggregateKind);
        Assert.Equal("1", issueEntry.SourceAggregateId);

        var workflowEntry = byType["com.mohist.workflow.stage.started"];
        Assert.Equal("workflowrun", workflowEntry.Origin);
        Assert.Equal("workflow-run", workflowEntry.SourceAggregateKind);
        Assert.Equal(workflowRunId, workflowEntry.SourceAggregateId);
        Assert.Equal(1, workflowEntry.IssueNumber);

        var sessionEntry = byType["com.mohist.agent-session.runtime-bound"];
        Assert.Equal("agentsession", sessionEntry.Origin);
        Assert.Equal("agent-session", sessionEntry.SourceAggregateKind);
        Assert.Equal(sessionId, sessionEntry.SourceAggregateId);

        Assert.False(byType.ContainsKey("com.mohist.epic.created"));

        var times = response.Select(e => DateTimeOffset.Parse(e.Time)).ToList();
        for (var i = 1; i < times.Count; i++)
            Assert.True(times[i - 1] >= times[i], $"Expected descending order but {times[i - 1]:o} < {times[i]:o}");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GetProjectEvents_DefaultLimit_ReturnsMostRecentFirst()
    {
        var project = await CreateProjectAsync();
        await SeedIssueAsync(project.Id, number: 1);

        var t0 = FixedTime.AddHours(-10);
        for (var i = 0; i < 205; i++)
        {
            await AppendIssueEventAsync(project.Id, 1, $"test.event-{i}",
                time: t0.AddMinutes(i), subject: "1");
        }

        var response = await _client.GetDataAsync<List<ProjectEventResponseDto>>(
            $"/api/projects/{project.Id}/events");

        Assert.Equal(200, response.Count);
        Assert.Equal("test.event-204", response[0].Type);
        Assert.Equal("test.event-5", response[^1].Type);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GetProjectEvents_UsesWorkflowStoreMetadataForWorkflowContext()
    {
        var project = await CreateProjectAsync("workflow-context");
        var workflowRunId = $"wf_{Guid.NewGuid():N}";
        await SeedIssueAsync(project.Id, number: 42);

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IWorkflowRunStore>();
            await store.SaveAsync(new WorkflowRun
            {
                Id = workflowRunId,
                Metadata = new WorkflowRunMetadata(
                    Name: null,
                    CreatedAt: FixedTime,
                    Annotations: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["projectId"] = project.Id,
                        ["issueNumber"] = "42",
                    }),
                Stages = [],
            }, [new WorkflowRunFailed("failed")]);
        }

        var response = await _client.GetDataAsync<List<ProjectEventResponseDto>>(
            $"/api/projects/{project.Id}/events");

        var workflow = Assert.Single(response, entry => entry.Type == "com.mohist.workflow.run.failed");
        Assert.Equal(42, workflow.IssueNumber);
        Assert.Equal(workflowRunId, workflow.SourceAggregateId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GetProjectEvents_ProjectsPersistedSessionLifecycleWithHistoricalContext()
    {
        var project = await CreateProjectAsync("session-lifecycle");
        var sessionId = $"agent_session_{Guid.NewGuid():N}";
        await SeedAgentSessionAsync(project.Id, sessionId);
        await AppendSessionClosedFactAsync(sessionId, FixedTime.AddMinutes(1));

        var response = await _client.GetDataAsync<List<ProjectEventResponseDto>>(
            $"/api/projects/{project.Id}/events");

        var opened = Assert.Single(response, entry => entry.Type == "coder_session_started");
        Assert.Equal("workflow", opened.SessionSourceKind);
        Assert.Equal(1, opened.IssueNumber);
        Assert.Equal("wf-1", opened.WorkflowRunId);
        Assert.Equal("runner-1", opened.RunnerId);

        var closed = Assert.Single(response, entry => entry.Type == "session.closed");
        Assert.Equal("failed", closed.Data.GetProperty("status").GetString());
        Assert.Equal("runner timeout", closed.Data.GetProperty("failureReason").GetString());
        Assert.Equal("workflow", closed.SessionSourceKind);
        Assert.Equal(1, closed.IssueNumber);
        Assert.Equal("runner-1", closed.RunnerId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GetProjectEvents_WithExplicitLimit_CapsReturnedRows()
    {
        var project = await CreateProjectAsync();
        await SeedIssueAsync(project.Id, number: 1);

        var t0 = FixedTime.AddHours(-2);
        for (var i = 0; i < 5; i++)
        {
            await AppendIssueEventAsync(project.Id, 1, $"test.event-{i}",
                time: t0.AddMinutes(i), subject: "1");
        }

        var response = await _client.GetDataAsync<List<ProjectEventResponseDto>>(
            $"/api/projects/{project.Id}/events?limit=2");

        Assert.Equal(2, response.Count);
        Assert.Equal("test.event-4", response[0].Type);
        Assert.Equal("test.event-3", response[1].Type);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GetProjectEvents_DoesNotLeakEventsFromOtherProjects()
    {
        var projectA = await CreateProjectAsync("scope-a");
        var projectB = await CreateProjectAsync("scope-b");

        await SeedIssueAsync(projectA.Id, number: 1);
        await SeedIssueAsync(projectB.Id, number: 1);

        await AppendIssueEventAsync(projectA.Id, 1, "com.mohist.issue.created",
            subject: "1");
        await AppendIssueEventAsync(projectB.Id, 1, "com.mohist.issue.created",
            subject: "1");

        var responseA = await _client.GetDataAsync<List<ProjectEventResponseDto>>(
            $"/api/projects/{projectA.Id}/events");
        var responseB = await _client.GetDataAsync<List<ProjectEventResponseDto>>(
            $"/api/projects/{projectB.Id}/events");

        Assert.Single(responseA);
        Assert.Equal("1", responseA[0].SourceAggregateId);

        Assert.Single(responseB);
        Assert.Equal("1", responseB[0].SourceAggregateId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GetProjectEvents_DoesNotLeakAgentSessionsFromOtherProjects()
    {
        var projectA = await CreateProjectAsync("agent-scope-a");
        var projectB = await CreateProjectAsync("agent-scope-b");

        var sessionAId = $"agent_session_{Guid.NewGuid():N}";
        var sessionBId = $"agent_session_{Guid.NewGuid():N}";
        await SeedAgentSessionAsync(projectA.Id, sessionAId);
        await SeedAgentSessionAsync(projectB.Id, sessionBId);

        await AppendAgentSessionEventAsync(sessionAId, projectA.Id,
            "com.mohist.agent-session.runtime-bound", subject: sessionAId);
        await AppendAgentSessionEventAsync(sessionBId, projectB.Id,
            "com.mohist.agent-session.runtime-bound", subject: sessionBId);

        var responseA = await _client.GetDataAsync<List<ProjectEventResponseDto>>(
            $"/api/projects/{projectA.Id}/events");
        var responseB = await _client.GetDataAsync<List<ProjectEventResponseDto>>(
            $"/api/projects/{projectB.Id}/events");

        Assert.All(responseA, entry => Assert.Equal(sessionAId, entry.SourceAggregateId));
        Assert.Contains(responseA, entry => entry.Type == "com.mohist.agent-session.runtime-bound" && entry.Origin == "agentsession");

        Assert.All(responseB, entry => Assert.Equal(sessionBId, entry.SourceAggregateId));
        Assert.DoesNotContain(responseB, e => e.SourceAggregateId == sessionAId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GetProjectEvents_ForProjectWithNoEvents_ReturnsEmptyList()
    {
        var project = await CreateProjectAsync("empty");

        var response = await _client.GetDataAsync<List<ProjectEventResponseDto>>(
            $"/api/projects/{project.Id}/events");

        Assert.NotNull(response);
        Assert.Empty(response);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GetProjectEvents_UnknownProject_Returns404()
    {
        using var response = await _client.GetAsync(
            $"/api/projects/proj_does_not_exist_{Guid.NewGuid():N}/events");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GetProjectEvents_RejectsEventTypesWithoutARecordedSource()
    {
        var project = await CreateProjectAsync("invalid-types");

        using var response = await _client.GetAsync($"/api/projects/{project.Id}/events?types=runner");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var emptyResponse = await _client.GetAsync($"/api/projects/{project.Id}/events?types=,");
        Assert.Equal(HttpStatusCode.BadRequest, emptyResponse.StatusCode);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GetProjectEvents_DoesNotCreateAnyNewEvents()
    {
        var project = await CreateProjectAsync("no-new-events");
        await SeedIssueAsync(project.Id, number: 1);

        var t0 = FixedTime.AddMinutes(-5);
        await AppendIssueEventAsync(project.Id, 1, "com.mohist.issue.created",
            time: t0, subject: "1");

        await using var db = await _fixture.Services
            .GetRequiredService<IDbContextFactory<MohistDbContext>>()
            .CreateDbContextAsync();

        var issueCountBefore = await db.IssueEvents.AsNoTracking()
            .CountAsync();
        var workflowCountBefore = await db.WorkflowRunEvents.AsNoTracking()
            .CountAsync();
        var sessionCountBefore = await db.AgentSessionEvents.AsNoTracking()
            .CountAsync();

        var response = await _client.GetDataAsync<List<ProjectEventResponseDto>>(
            $"/api/projects/{project.Id}/events");

        Assert.Single(response);

        Assert.Equal(issueCountBefore, await db.IssueEvents.AsNoTracking().CountAsync());
        Assert.Equal(workflowCountBefore, await db.WorkflowRunEvents.AsNoTracking().CountAsync());
        Assert.Equal(sessionCountBefore, await db.AgentSessionEvents.AsNoTracking().CountAsync());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GetProjectEvents_ProjectsOnlyActivitySafePayloadFields()
    {
        var project = await CreateProjectAsync("payload");
        await SeedIssueAsync(project.Id, number: 7);

        var t0 = FixedTime.AddMinutes(-5);
        await AppendIssueEventAsync(project.Id, 7, "com.mohist.issue.work-started",
            time: t0,
            subject: "7",
            data: new { stage = "build", coderSessionId = "legacy-session", attempt = 1, internalTrace = "not for activity" });

        var response = await _client.GetDataAsync<List<ProjectEventResponseDto>>(
            $"/api/projects/{project.Id}/events");

        var entry = Assert.Single(response);
        Assert.Equal("com.mohist.issue.work-started", entry.Type);
        Assert.Equal("7", entry.Subject);
        Assert.Equal("1.0", entry.SpecVersion);
        Assert.Equal("application/json", entry.DataContentType);
        Assert.Equal(JsonValueKind.Object, entry.Data.ValueKind);
        Assert.Equal("build", entry.Data.GetProperty("stage").GetString());
        Assert.False(entry.Data.TryGetProperty("coderSessionId", out _));
        Assert.False(entry.Data.TryGetProperty("attempt", out _));
        Assert.False(entry.Data.TryGetProperty("internalTrace", out _));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GetProjectEvents_DoesNotExposeEnvelopeExtensions()
    {
        var project = await CreateProjectAsync("no-sub");
        await SeedIssueAsync(project.Id, number: 1);

        var t0 = FixedTime.AddMinutes(-5);
        await AppendIssueEventAsync(project.Id, 1, "com.mohist.issue.created",
            time: t0, subject: "1");

        using var response = await _client.GetAsync($"/api/projects/{project.Id}/events");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var entry = Assert.Single(document.RootElement.GetProperty("data").EnumerateArray());
        Assert.False(entry.TryGetProperty("extensions", out _));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GetProjectEvents_AttentionFilter_FindsOlderFailureBeyondRoutineWindow()
    {
        var project = await CreateProjectAsync("filtered-history");
        var workflowRunId = $"wf_{Guid.NewGuid():N}";
        await SeedIssueAsync(project.Id, number: 1);
        await SeedWorkflowRunAsync(project.Id, workflowRunId, issueNumber: 1);

        await AppendWorkflowEventAsync(workflowRunId, project.Id, 1,
            "com.mohist.workflow.stage.failed", time: FixedTime.AddHours(-2));
        for (var i = 0; i < 205; i++)
        {
            await AppendWorkflowEventAsync(workflowRunId, project.Id, 1,
                "com.mohist.workflow.stage.started", time: FixedTime.AddMinutes(i));
        }

        var response = await _client.GetDataAsync<List<ProjectEventResponseDto>>(
            $"/api/projects/{project.Id}/events?types=failure&attentionOnly=true");

        var failure = Assert.Single(response);
        Assert.Equal("com.mohist.workflow.stage.failed", failure.Type);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GetProjectEvents_FailureFilter_FindsOlderStatusFailureBeyondRoutineWindow()
    {
        var project = await CreateProjectAsync("status-filtered-history");
        var sessionId = $"agent_session_{Guid.NewGuid():N}";
        await SeedAgentSessionAsync(project.Id, sessionId);

        await AppendAgentSessionEventAsync(
            sessionId,
            project.Id,
            "coder_session_status_changed",
            time: FixedTime.AddHours(-2),
            data: new { status = "failed" });
        for (var i = 0; i < 205; i++)
        {
            await AppendAgentSessionEventAsync(
                sessionId,
                project.Id,
                "coder_session_status_changed",
                time: FixedTime.AddMinutes(i),
                data: new { status = "active" });
        }

        var response = await _client.GetDataAsync<List<ProjectEventResponseDto>>(
            $"/api/projects/{project.Id}/events?types=failure&attentionOnly=true");

        var failure = Assert.Single(response);
        Assert.Equal("coder_session_status_changed", failure.Type);
        Assert.Equal("failed", failure.Data.GetProperty("status").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GetProjectEvents_LargeHistory_ReturnsOnlyTheRequestedBoundedWindow()
    {
        var project = await CreateProjectAsync("large-history");
        await SeedIssueAsync(project.Id, number: 1);
        await SeedIssueEventHistoryAsync(project.Id, 1, 1_000);

        var response = await _client.GetDataAsync<List<ProjectEventResponseDto>>(
            $"/api/projects/{project.Id}/events?limit=2");

        Assert.Collection(
            response,
            eventEntry => Assert.Equal(1_000, eventEntry.Id),
            eventEntry => Assert.Equal(999, eventEntry.Id));

        var maxResponse = await _client.GetDataAsync<List<ProjectEventResponseDto>>(
            $"/api/projects/{project.Id}/events?limit=1001");
        var defaultLimitResponse = await _client.GetDataAsync<List<ProjectEventResponseDto>>(
            $"/api/projects/{project.Id}/events?limit=0");

        Assert.Equal(1_000, maxResponse.Count);
        Assert.Equal(200, defaultLimitResponse.Count);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GetProjectEvents_LimitOne_UsesStableCompleteTieBreakAcrossAggregates()
    {
        var project = await CreateProjectAsync("stable-order");
        var workflowRunId = $"wf_{Guid.NewGuid():N}";
        await SeedIssueAsync(project.Id, number: 1);
        await SeedWorkflowRunAsync(project.Id, workflowRunId, issueNumber: 1);

        await AppendIssueEventAsync(project.Id, 1, "com.mohist.issue.created", time: FixedTime, subject: "1");
        await AppendWorkflowEventAsync(workflowRunId, project.Id, 1,
            "com.mohist.workflow.stage.started", time: FixedTime);

        var first = await _client.GetDataAsync<List<ProjectEventResponseDto>>(
            $"/api/projects/{project.Id}/events?limit=1");
        var second = await _client.GetDataAsync<List<ProjectEventResponseDto>>(
            $"/api/projects/{project.Id}/events?limit=1");

        Assert.Equal("com.mohist.issue.created", Assert.Single(first).Type);
        Assert.Equal(first[0].EnvelopeId, Assert.Single(second).EnvelopeId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GetProjectEvents_SubSecondTimes_AreOrderedByFractionalPrecision()
    {
        var project = await CreateProjectAsync("subsecond");
        await SeedIssueAsync(project.Id, number: 1);

        var baseTime = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        await AppendIssueEventAsync(project.Id, 1, "com.mohist.issue.created",
            time: baseTime.AddMilliseconds(900), subject: "late");
        await AppendIssueEventAsync(project.Id, 1, "com.mohist.issue.work-started",
            time: baseTime.AddMilliseconds(100), subject: "early");
        await AppendIssueEventAsync(project.Id, 1, "com.mohist.issue.completed",
            time: baseTime.AddMilliseconds(500), subject: "mid");

        var response = await _client.GetDataAsync<List<ProjectEventResponseDto>>(
            $"/api/projects/{project.Id}/events?limit=3");

        Assert.Collection(
            response,
            entry => Assert.Equal("late", entry.Subject),
            entry => Assert.Equal("mid", entry.Subject),
            entry => Assert.Equal("early", entry.Subject));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GetProjectEvents_ProjectsScalarAndArrayPayloadsAsEmptyObjects()
    {
        var project = await CreateProjectAsync("json-payloads");
        await SeedIssueAsync(project.Id, number: 1);

        await AppendIssueEventAsync(project.Id, 1, "com.mohist.issue.created", data: "created");
        await AppendIssueEventAsync(project.Id, 1, "com.mohist.issue.work-started", data: new[] { "build", "check" });

        var response = await _client.GetDataAsync<List<ProjectEventResponseDto>>(
            $"/api/projects/{project.Id}/events");

        Assert.All(response, entry =>
        {
            Assert.Equal(JsonValueKind.Object, entry.Data.ValueKind);
            Assert.Empty(entry.Data.EnumerateObject());
        });
    }

    private async Task<ProjectDto> CreateProjectAsync(string nameSuffix = "events")
    {
        var name = $"{nameSuffix}-{Guid.NewGuid():N}";
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", name);
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new
        {
            name = "main",
            gitUrl = $"file://{Guid.NewGuid():N}",
            baseBranch = "main",
            setDefault = true,
        });
        return project;
    }

    private async Task SeedIssueAsync(string projectId, int number)
    {
        var issue = new DomainIssue
        {
            ProjectId = projectId,
            Number = number,
            Title = $"Issue #{number}",
            Status = Mohist.Server.Issue.Domain.IssueStatus.InProgress,
        };
        await using var db = await _fixture.Services
            .GetRequiredService<IDbContextFactory<MohistDbContext>>()
            .CreateDbContextAsync();
        db.Issues.Add(new Mohist.Server.Infrastructure.Data.Issue.IssueRow
        {
            ProjectId = projectId,
            Number = number,
            State = Mohist.Server.Infrastructure.Data.Issue.IssueStore.Serialize(issue),
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedWorkflowRunAsync(string projectId, string workflowRunId, int issueNumber)
    {
        await using var db = await _fixture.Services
            .GetRequiredService<IDbContextFactory<MohistDbContext>>()
            .CreateDbContextAsync();
        var state = JsonSerializer.Serialize(new
        {
            id = workflowRunId,
            metadata = new
            {
                createdAt = FixedTime,
                name = "test",
                annotations = new Dictionary<string, string>
                {
                    ["projectId"] = projectId,
                    ["issueNumber"] = issueNumber.ToString(),
                },
            },
            status = "Running",
            currentStageId = "build",
            stages = Array.Empty<object>(),
        });
        db.WorkflowRuns.Add(new WorkflowRunRow
        {
            WorkflowRunId = workflowRunId,
            State = state,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedAgentSessionAsync(string projectId, string sessionId)
    {
        await using var db = await _fixture.Services
            .GetRequiredService<IDbContextFactory<MohistDbContext>>()
            .CreateDbContextAsync();
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
            [AgentSessionQueryMetadataKeys.SourceKind] = "workflow",
            [AgentSessionQueryMetadataKeys.IssueNumber] = "1",
            [AgentSessionQueryMetadataKeys.WorkflowRunId] = "wf-1",
        };
        var state = JsonSerializer.Serialize(new
        {
            id = sessionId,
            metadata = new { labels },
            settings = new { model = "gpt-4o" },
            status = new { createdAt = FixedTime },
        }, Mohist.Server.Infrastructure.JSON.Options);
        db.AgentSessions.Add(new AgentSessionRow
        {
            Id = sessionId,
            State = state,
            Status = "opened",
            CreatedAt = FixedTime.UtcDateTime,
            RunnerId = "runner-1",
        });
        await db.SaveChangesAsync();
    }

    private async Task AppendSessionClosedFactAsync(string sessionId, DateTimeOffset time)
    {
        await using var db = await _fixture.Services
            .GetRequiredService<IDbContextFactory<MohistDbContext>>()
            .CreateDbContextAsync();
        var turn = new AgentSessionTranscriptTurnRow
        {
            SessionId = sessionId,
            Sequence = 1,
            StartedAt = time.UtcDateTime,
            UpdatedAt = time.UtcDateTime,
        };
        db.AgentSessionTranscriptTurns.Add(turn);
        await db.SaveChangesAsync();

        db.AgentSessionTranscriptParts.Add(new AgentSessionTranscriptPartRow
        {
            TurnId = turn.Id,
            Sequence = 1,
            Type = TranscriptPartTypes.SessionClosed,
            CorrelationKey = $"session.closed_{Guid.NewGuid():N}",
            PayloadJson = """{"status":"failed","failureReason":"runner timeout","exitCode":1}""",
            FirstSeenAt = time.UtcDateTime,
            LastSeenAt = time.UtcDateTime,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedEpicAsync(string projectId, int number)
    {
        await using var db = await _fixture.Services
            .GetRequiredService<IDbContextFactory<MohistDbContext>>()
            .CreateDbContextAsync();
        db.Epics.Add(new Mohist.Server.Infrastructure.Data.Epic.EpicRow
        {
            ProjectId = projectId,
            Number = number,
            Title = $"Epic #{number}",
            Description = "",
            Priority = "p2",
            Status = "active",
            CreatedAt = FixedTime,
            UpdatedAt = FixedTime,
        });
        await db.SaveChangesAsync();
    }

    private async Task AppendIssueEventAsync(
        string projectId,
        int issueNumber,
        string type,
        DateTimeOffset? time = null,
        string? subject = null,
        object? data = null)
    {
        await AppendEventAsync(
            IssueEventPersistence.IssueSource(projectId, issueNumber),
            projectId,
            type,
            time ?? FixedTime,
            subject,
            data,
            extensions: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["projectid"] = projectId,
                ["issueno"] = issueNumber.ToString(),
            });
    }

    private async Task AppendWorkflowEventAsync(
        string workflowRunId,
        string projectId,
        int issueNumber,
        string type,
        DateTimeOffset? time = null,
        string? subject = null,
        object? data = null)
    {
        await AppendEventAsync(
            WorkflowRunEventPersistence.WorkflowRunSource(workflowRunId),
            projectId,
            type,
            time ?? FixedTime,
            subject,
            data,
            extensions: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["projectid"] = projectId,
                ["issueno"] = issueNumber.ToString(),
                ["workflowrunid"] = workflowRunId,
                ["stage"] = "test",
            });
    }

    private async Task AppendAgentSessionEventAsync(
        string sessionId,
        string projectId,
        string type,
        DateTimeOffset? time = null,
        string? subject = null,
        object? data = null)
    {
        await AppendEventAsync(
            AgentSessionEventPersistence.AgentSessionSource(sessionId),
            projectId,
            type,
            time ?? FixedTime,
            subject,
            data,
            extensions: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["projectid"] = projectId,
                ["sessionid"] = sessionId,
            });
    }

    private async Task AppendEpicEventAsync(
        string projectId,
        int epicNumber,
        string type,
        DateTimeOffset? time = null,
        string? subject = null,
        object? data = null)
    {
        await AppendEventAsync(
            EpicEventPersistence.EpicSource(projectId, epicNumber),
            projectId,
            type,
            time ?? FixedTime,
            subject,
            data,
            extensions: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["projectid"] = projectId,
                ["epicno"] = epicNumber.ToString(),
            });
    }

    private async Task AppendEventAsync(
        string source,
        string projectId,
        string type,
        DateTimeOffset time,
        string? subject,
        object? data,
        Dictionary<string, string> extensions)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IEventStore>();
        var dataElement = data is null
            ? JsonDocument.Parse("null").RootElement.Clone()
            : JsonSerializer.SerializeToElement(data, CloudEvent.JsonOptions);
        var envelope = new CloudEvent(
            id: Guid.NewGuid().ToString(),
            source: new Uri(source, UriKind.Relative),
            type: type,
            time: time,
            data: dataElement,
            subject: subject,
            extensions: extensions);
        await store.AppendAsync(envelope);
    }

    private async Task SeedIssueEventHistoryAsync(string projectId, int issueNumber, int count)
    {
        await using var db = await _fixture.Services
            .GetRequiredService<IDbContextFactory<MohistDbContext>>()
            .CreateDbContextAsync();
        var source = IssueEventPersistence.IssueSource(projectId, issueNumber);
        db.IssueEvents.AddRange(Enumerable.Range(1, count).Select(index => new IssueEventRow
        {
            Id = index,
            Source = source,
            EventId = $"history-{index}",
            Type = "com.mohist.issue.created",
            Time = FixedTime.AddSeconds(index),
            SpecVersion = "1.0",
            DataContentType = "application/json",
            Data = JsonSerializer.SerializeToElement(new { }, CloudEvent.JsonOptions),
            ExtensionsJson = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["projectid"] = projectId,
                ["issueno"] = issueNumber.ToString(),
            }),
        }));
        await db.SaveChangesAsync();
    }

    private sealed record ProjectDto(string Id, string Name);

    private sealed record ProjectEventResponseDto(
        long Id,
        string Origin,
        string SourceAggregateKind,
        string SourceAggregateId,
        string Source,
        string Type,
        string Time,
        string EnvelopeId,
        string SpecVersion,
        string? Subject,
        string? DataContentType,
        JsonElement Data,
        string? RunnerId,
        int? IssueNumber,
        string? SessionSourceKind,
        string? WorkflowRunId,
        string? AgentId,
        string? AgentName);
}

public class ProjectEventsModelDebug
{
    [Fact]
    public void DebugPendingModelChanges()
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        using var db = new MohistDbContext(options);
        var differ = db.GetService<IMigrationsModelDiffer>();
        var initializer = db.GetService<IModelRuntimeInitializer>();
        var operations = differ.GetDifferences(
            initializer.Initialize(new MohistDbContextModelSnapshot().Model, designTime: true).GetRelationalModel(),
            db.GetService<IDesignTimeModel>().Model.GetRelationalModel());

        Assert.Empty(operations);
    }
}
