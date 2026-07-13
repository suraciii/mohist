using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.SpecTests.Specs.Api;

/// <summary>
/// Specs for issue-402 T-000 — the project-scoped event read endpoint
/// (<c>GET /api/projects/&#123;projectRef&#125;/events</c>) that powers the
/// Activity evidence view. The endpoint queries already-recorded events
/// from <c>IssueEvents</c>, <c>WorkflowRunEvents</c>,
/// <c>AgentSessionEvents</c>, and <c>EpicEvents</c> without changing how
/// events are recorded, emitted, or subscribed, so these specs verify
/// cross-aggregate retrieval, time ordering, project scoping, and the
/// read-only contract.
/// </summary>
[Collection("IntegrationApi")]
public class ProjectEventsApiSpecs
{
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
        var issueId = $"issue_{Guid.NewGuid():N}";
        var workflowRunId = $"wf_{Guid.NewGuid():N}";
        var sessionId = $"agent_session_{Guid.NewGuid():N}";
        var epicId = $"epic_{Guid.NewGuid():N}";

        await SeedIssueAsync(project.Id, issueId, number: 1);
        await SeedWorkflowRunAsync(project.Id, workflowRunId, issueId);
        await SeedAgentSessionAsync(project.Id, sessionId);
        await SeedEpicAsync(project.Id, epicId, number: 1);

        var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);
        await AppendIssueEventAsync(issueId, project.Id, "com.mohist.issue.created",
            time: t0, subject: "1");
        await AppendWorkflowEventAsync(workflowRunId, project.Id, issueId,
            "com.mohist.workflow.stage.started",
            time: t0.AddMinutes(1), subject: "1");
        await AppendAgentSessionEventAsync(sessionId, project.Id,
            "com.mohist.agent-session.runtime-bound",
            time: t0.AddMinutes(2), subject: sessionId);
        await AppendEpicEventAsync(epicId, project.Id, "com.mohist.epic.created",
            time: t0.AddMinutes(3), subject: "1");

        var response = await _client.GetDataAsync<List<ProjectEventResponseDto>>(
            $"/api/projects/{project.Id}/events");

        Assert.Equal(4, response.Count);

        var byType = response.ToDictionary(e => e.Type);
        Assert.True(byType.ContainsKey("com.mohist.issue.created"));
        Assert.True(byType.ContainsKey("com.mohist.workflow.stage.started"));
        Assert.True(byType.ContainsKey("com.mohist.agent-session.runtime-bound"));
        Assert.True(byType.ContainsKey("com.mohist.epic.created"));

        var issueEntry = byType["com.mohist.issue.created"];
        Assert.Equal("issue", issueEntry.Origin);
        Assert.Equal("issue", issueEntry.SourceAggregateKind);
        Assert.Equal(issueId, issueEntry.SourceAggregateId);

        var workflowEntry = byType["com.mohist.workflow.stage.started"];
        Assert.Equal("workflowrun", workflowEntry.Origin);
        Assert.Equal("workflow-run", workflowEntry.SourceAggregateKind);
        Assert.Equal(workflowRunId, workflowEntry.SourceAggregateId);

        var sessionEntry = byType["com.mohist.agent-session.runtime-bound"];
        Assert.Equal("agentsession", sessionEntry.Origin);
        Assert.Equal("agent-session", sessionEntry.SourceAggregateKind);
        Assert.Equal(sessionId, sessionEntry.SourceAggregateId);

        var epicEntry = byType["com.mohist.epic.created"];
        Assert.Equal("epic", epicEntry.Origin);
        Assert.Equal("epic", epicEntry.SourceAggregateKind);
        Assert.Equal(epicId, epicEntry.SourceAggregateId);

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
        var issueId = $"issue_{Guid.NewGuid():N}";
        await SeedIssueAsync(project.Id, issueId, number: 1);

        var t0 = DateTimeOffset.UtcNow.AddHours(-2);
        for (var i = 0; i < 5; i++)
        {
            await AppendIssueEventAsync(issueId, project.Id, $"com.mohist.test.event-{i}",
                time: t0.AddMinutes(i), subject: "1");
        }

        var response = await _client.GetDataAsync<List<ProjectEventResponseDto>>(
            $"/api/projects/{project.Id}/events");

        Assert.Equal(5, response.Count);
        Assert.Equal("com.mohist.test.event-4", response[0].Type);
        Assert.Equal("com.mohist.test.event-3", response[1].Type);
        Assert.Equal("com.mohist.test.event-2", response[2].Type);
        Assert.Equal("com.mohist.test.event-1", response[3].Type);
        Assert.Equal("com.mohist.test.event-0", response[4].Type);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GetProjectEvents_WithExplicitLimit_CapsReturnedRows()
    {
        var project = await CreateProjectAsync();
        var issueId = $"issue_{Guid.NewGuid():N}";
        await SeedIssueAsync(project.Id, issueId, number: 1);

        var t0 = DateTimeOffset.UtcNow.AddHours(-2);
        for (var i = 0; i < 5; i++)
        {
            await AppendIssueEventAsync(issueId, project.Id, $"com.mohist.test.event-{i}",
                time: t0.AddMinutes(i), subject: "1");
        }

        var response = await _client.GetDataAsync<List<ProjectEventResponseDto>>(
            $"/api/projects/{project.Id}/events?limit=2");

        Assert.Equal(2, response.Count);
        Assert.Equal("com.mohist.test.event-4", response[0].Type);
        Assert.Equal("com.mohist.test.event-3", response[1].Type);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GetProjectEvents_DoesNotLeakEventsFromOtherProjects()
    {
        var projectA = await CreateProjectAsync("scope-a");
        var projectB = await CreateProjectAsync("scope-b");

        var issueAId = $"issue_{Guid.NewGuid():N}";
        var issueBId = $"issue_{Guid.NewGuid():N}";
        await SeedIssueAsync(projectA.Id, issueAId, number: 1);
        await SeedIssueAsync(projectB.Id, issueBId, number: 1);

        await AppendIssueEventAsync(issueAId, projectA.Id, "com.mohist.issue.created",
            subject: "1");
        await AppendIssueEventAsync(issueBId, projectB.Id, "com.mohist.issue.created",
            subject: "1");

        var responseA = await _client.GetDataAsync<List<ProjectEventResponseDto>>(
            $"/api/projects/{projectA.Id}/events");
        var responseB = await _client.GetDataAsync<List<ProjectEventResponseDto>>(
            $"/api/projects/{projectB.Id}/events");

        Assert.Single(responseA);
        Assert.Equal(issueAId, responseA[0].SourceAggregateId);
        Assert.Equal(projectA.Id, responseA[0].Extensions["projectid"]);

        Assert.Single(responseB);
        Assert.Equal(issueBId, responseB[0].SourceAggregateId);
        Assert.Equal(projectB.Id, responseB[0].Extensions["projectid"]);
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

        Assert.Single(responseA);
        Assert.Equal(sessionAId, responseA[0].SourceAggregateId);
        Assert.Equal("agentsession", responseA[0].Origin);

        Assert.Single(responseB);
        Assert.Equal(sessionBId, responseB[0].SourceAggregateId);
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
    public async Task GetProjectEvents_DoesNotCreateAnyNewEvents()
    {
        var project = await CreateProjectAsync("no-new-events");
        var issueId = $"issue_{Guid.NewGuid():N}";
        await SeedIssueAsync(project.Id, issueId, number: 1);

        var t0 = DateTimeOffset.UtcNow.AddMinutes(-5);
        await AppendIssueEventAsync(issueId, project.Id, "com.mohist.issue.created",
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
        var epicCountBefore = await db.EpicEvents.AsNoTracking()
            .CountAsync();

        var response = await _client.GetDataAsync<List<ProjectEventResponseDto>>(
            $"/api/projects/{project.Id}/events");

        Assert.Single(response);

        Assert.Equal(issueCountBefore, await db.IssueEvents.AsNoTracking().CountAsync());
        Assert.Equal(workflowCountBefore, await db.WorkflowRunEvents.AsNoTracking().CountAsync());
        Assert.Equal(sessionCountBefore, await db.AgentSessionEvents.AsNoTracking().CountAsync());
        Assert.Equal(epicCountBefore, await db.EpicEvents.AsNoTracking().CountAsync());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GetProjectEvents_PreservesEnvelopePayloadAndExtensions()
    {
        var project = await CreateProjectAsync("payload");
        var issueId = $"issue_{Guid.NewGuid():N}";
        await SeedIssueAsync(project.Id, issueId, number: 7);

        var t0 = DateTimeOffset.UtcNow.AddMinutes(-5);
        await AppendIssueEventAsync(issueId, project.Id, "com.mohist.issue.work-started",
            time: t0,
            subject: "7",
            data: new { stage = "build", attempt = 1 });

        var response = await _client.GetDataAsync<List<ProjectEventResponseDto>>(
            $"/api/projects/{project.Id}/events");

        var entry = Assert.Single(response);
        Assert.Equal("com.mohist.issue.work-started", entry.Type);
        Assert.Equal("7", entry.Subject);
        Assert.Equal("1.0", entry.SpecVersion);
        Assert.Equal("application/json", entry.DataContentType);
        Assert.Equal(JsonValueKind.Object, entry.Data.ValueKind);
        Assert.Equal("build", entry.Data.GetProperty("stage").GetString());
        Assert.Equal(1, entry.Data.GetProperty("attempt").GetInt32());

        Assert.True(entry.Extensions.ContainsKey("projectid"));
        Assert.Equal(project.Id, entry.Extensions["projectid"]);
        Assert.True(entry.Extensions.ContainsKey("issueid"));
        Assert.Equal(issueId, entry.Extensions["issueid"]);
        Assert.Equal(7, int.Parse(entry.Extensions["issueno"]));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GetProjectEvents_DoesNotIncludeSubscribedOrDispatchedMarkers()
    {
        var project = await CreateProjectAsync("no-sub");
        var issueId = $"issue_{Guid.NewGuid():N}";
        await SeedIssueAsync(project.Id, issueId, number: 1);

        var t0 = DateTimeOffset.UtcNow.AddMinutes(-5);
        await AppendIssueEventAsync(issueId, project.Id, "com.mohist.issue.created",
            time: t0, subject: "1");

        var response = await _client.GetDataAsync<List<ProjectEventResponseDto>>(
            $"/api/projects/{project.Id}/events");

        var entry = Assert.Single(response);
        Assert.True(entry.Id > 0);
        Assert.False(entry.Extensions.ContainsKey("subscribers"));
        Assert.False(entry.Extensions.ContainsKey("subscriptions"));
        Assert.False(entry.Extensions.ContainsKey("subscribedAt"));
        Assert.False(entry.Extensions.ContainsKey("dispatchedAt"));
    }

    private async Task<ProjectDto> CreateProjectAsync(string nameSuffix = "events")
    {
        var name = $"{nameSuffix}-{Guid.NewGuid():N}";
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name });
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new
        {
            name = "main",
            gitUrl = $"file://{Guid.NewGuid():N}",
            baseBranch = "main",
            isDefault = true,
        });
        return project;
    }

    private async Task SeedIssueAsync(string projectId, string issueId, int number)
    {
        var issue = new DomainIssue
        {
            Id = issueId,
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
            IssueId = issue.Id,
            State = Mohist.Server.Infrastructure.Data.Issue.IssueStore.Serialize(issue),
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedWorkflowRunAsync(string projectId, string workflowRunId, string issueId)
    {
        await using var db = await _fixture.Services
            .GetRequiredService<IDbContextFactory<MohistDbContext>>()
            .CreateDbContextAsync();
        var state = JsonSerializer.Serialize(new
        {
            id = workflowRunId,
            metadata = new
            {
                createdAt = DateTimeOffset.UtcNow,
                name = "test",
                annotations = new Dictionary<string, string>
                {
                    ["projectId"] = projectId,
                    ["issueId"] = issueId,
                    ["issueNumber"] = "1",
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
        };
        var state = JsonSerializer.Serialize(new
        {
            id = sessionId,
            metadata = new { labels },
            settings = new { model = "gpt-4o" },
            status = new { createdAt = DateTimeOffset.UtcNow },
        }, Mohist.Server.Infrastructure.JSON.Options);
        db.AgentSessions.Add(new AgentSessionRow
        {
            Id = sessionId,
            State = state,
            Status = "opened",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedEpicAsync(string projectId, string epicId, int number)
    {
        await using var db = await _fixture.Services
            .GetRequiredService<IDbContextFactory<MohistDbContext>>()
            .CreateDbContextAsync();
        db.Epics.Add(new Mohist.Server.Infrastructure.Data.Epic.EpicRow
        {
            Id = epicId,
            ProjectId = projectId,
            Number = number,
            Title = $"Epic #{number}",
            Description = "",
            Priority = "p2",
            Status = "active",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task AppendIssueEventAsync(
        string issueId,
        string projectId,
        string type,
        DateTimeOffset? time = null,
        string? subject = null,
        object? data = null)
    {
        await AppendEventAsync(
            IssueEventPersistence.IssueSource(issueId),
            projectId,
            type,
            time ?? DateTimeOffset.UtcNow,
            subject,
            data,
            extensions: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["projectid"] = projectId,
                ["issueid"] = issueId,
                ["issueno"] = subject ?? "1",
            });
    }

    private async Task AppendWorkflowEventAsync(
        string workflowRunId,
        string projectId,
        string issueId,
        string type,
        DateTimeOffset? time = null,
        string? subject = null,
        object? data = null)
    {
        await AppendEventAsync(
            WorkflowRunEventPersistence.WorkflowRunSource(workflowRunId),
            projectId,
            type,
            time ?? DateTimeOffset.UtcNow,
            subject,
            data,
            extensions: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["projectid"] = projectId,
                ["issueid"] = issueId,
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
            time ?? DateTimeOffset.UtcNow,
            subject,
            data,
            extensions: new Dictionary<string, string>(StringComparer.Ordinal));
    }

    private async Task AppendEpicEventAsync(
        string epicId,
        string projectId,
        string type,
        DateTimeOffset? time = null,
        string? subject = null,
        object? data = null)
    {
        await AppendEventAsync(
            EpicEventPersistence.EpicSource(epicId),
            projectId,
            type,
            time ?? DateTimeOffset.UtcNow,
            subject,
            data,
            extensions: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["projectid"] = projectId,
                ["epicid"] = epicId,
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

    private sealed record ProjectDto(string Id, string Name, string Path, string BaseBranch);

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
        Dictionary<string, string> Extensions,
        string? RunnerId);
}
