using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Migrations;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Api;

/// <summary>
/// Route-level contract specs for
/// <c>GET /api/projects/&#123;projectRef&#125;/events</c>: route reachability
/// (404 unknown project), parameter parsing (400 invalid <c>types</c>), and
/// one cross-aggregate success-path shape proving the DTO mapping surfaces
/// issue/workflow/agent-session events. The assembler's calculation matrix
/// (limit/cap/sort/tie-break/isolation/envelope-priority/activity-safe
/// projection/attention filters) lives in
/// <c>ProjectEventFeedAssemblerTests</c>.
/// </summary>
public class ProjectEventsApiSpecs : ProjectEventsApiTestSupport
{
    public ProjectEventsApiSpecs(MohistIntegrationFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task GetProjectEvents_ReturnsCrossAggregateEventsThroughTheDto()
    {
        var project = await CreateProjectAsync();
        var workflowRunId = $"wf_{Guid.NewGuid():N}";
        var sessionId = $"agent_session_{Guid.NewGuid():N}";

        await SeedIssueAsync(project.Id, number: 1);
        await SeedWorkflowRunAsync(project.Id, workflowRunId, issueNumber: 1);
        await SeedAgentSessionAsync(project.Id, sessionId);

        var t0 = FixedTime.AddMinutes(-10);
        await AppendIssueEventAsync(project.Id, 1, "com.mohist.issue.created", time: t0, subject: "1");
        await AppendWorkflowEventAsync(workflowRunId, project.Id, 1, "com.mohist.workflow.stage.started", time: t0.AddMinutes(1), subject: null);
        await AppendAgentSessionEventAsync(sessionId, project.Id, "com.mohist.agent-session.runtime-bound", time: t0.AddMinutes(2), subject: sessionId);

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
    }

    [Fact]
    public async Task GetProjectEvents_UnknownProject_Returns404()
    {
        using var response = await _client.GetAsync(
            $"/api/projects/proj_does_not_exist_{Guid.NewGuid():N}/events");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetProjectEvents_RejectsEventTypesWithoutARecordedSource()
    {
        var project = await CreateProjectAsync("invalid-types");

        using var response = await _client.GetAsync($"/api/projects/{project.Id}/events?types=runner");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var emptyResponse = await _client.GetAsync($"/api/projects/{project.Id}/events?types=,");
        Assert.Equal(HttpStatusCode.BadRequest, emptyResponse.StatusCode);
    }

    // The activity-safe payload projection lives in the route DTO mapping
    // (ProjectEventDto.ActivityData), not in ProjectEventFeedAssembler, so
    // it cannot sink to the assembler without a product-code extraction
    // (deferred to a later batch). These cases are the DTO-mapping contract
    // coverage called for by SINK-PLAN-289 §8.3.

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
        Assert.Equal("build", entry.Data.GetProperty("stage").GetString());
        Assert.False(entry.Data.TryGetProperty("coderSessionId", out _));
        Assert.False(entry.Data.TryGetProperty("attempt", out _));
        Assert.False(entry.Data.TryGetProperty("internalTrace", out _));
    }

    [Fact]
    public async Task GetProjectEvents_DoesNotExposeEnvelopeExtensions()
    {
        var project = await CreateProjectAsync("no-sub");
        await SeedIssueAsync(project.Id, number: 1);

        await AppendIssueEventAsync(project.Id, 1, "com.mohist.issue.created",
            time: FixedTime.AddMinutes(-5), subject: "1");

        using var response = await _client.GetAsync($"/api/projects/{project.Id}/events");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var entry = Assert.Single(document.RootElement.GetProperty("data").EnumerateArray());
        Assert.False(entry.TryGetProperty("extensions", out _));
    }

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
