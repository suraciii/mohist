using System.Text.Json;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;

namespace Mohist.Server.SpecTests.Specs.Api;

public abstract class ProjectEventsApiTestSupport
{
    protected static readonly DateTimeOffset FixedTime = ProjectEventSeedSupport.FixedTime;
    protected readonly MohistIntegrationFixture _fixture;
    protected readonly HttpClient _client;
    private readonly ProjectEventSeedSupport _seeds;

    protected ProjectEventsApiTestSupport(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
        _seeds = new ProjectEventSeedSupport(fixture.Services);
    }

    protected async Task<ProjectDto> CreateProjectAsync(string nameSuffix = "events")
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

    protected Task SeedIssueAsync(string projectId, int number) => _seeds.SeedIssueAsync(projectId, number);
    protected Task SeedWorkflowRunAsync(string projectId, string workflowRunId, int issueNumber) => _seeds.SeedWorkflowRunAsync(projectId, workflowRunId, issueNumber);
    protected Task SeedAgentSessionAsync(string projectId, string sessionId) => _seeds.SeedAgentSessionAsync(projectId, sessionId);
    protected Task SeedEpicAsync(string projectId, int number) => _seeds.SeedEpicAsync(projectId, number);
    protected Task AppendSessionActivityFactAsync(string sessionId, DateTimeOffset time, string status = "failed", string? failureReason = "runner timeout") => _seeds.AppendSessionActivityFactAsync(sessionId, time, status, failureReason);

    protected Task AppendIssueEventAsync(string projectId, int issueNumber, string type, DateTimeOffset? time = null, string? subject = null, object? data = null)
        => _seeds.AppendIssueEventAsync(projectId, issueNumber, type, time, subject, data);

    protected Task AppendWorkflowEventAsync(string workflowRunId, string projectId, int issueNumber, string type, DateTimeOffset? time = null, string? subject = null, object? data = null, int? envelopeIssueNumber = null)
        => _seeds.AppendWorkflowEventAsync(workflowRunId, projectId, issueNumber, type, time, subject, data, envelopeIssueNumber);

    protected Task AppendAgentSessionEventAsync(string sessionId, string projectId, string type, DateTimeOffset? time = null, string? subject = null, object? data = null, int? envelopeIssueNumber = 1, int? envelopeEpicNumber = 7, bool includeIssueContext = true)
        => _seeds.AppendAgentSessionEventAsync(sessionId, projectId, type, time, subject, data, envelopeIssueNumber, envelopeEpicNumber, includeIssueContext);

    protected Task AppendEpicEventAsync(string projectId, int epicNumber, string type, DateTimeOffset? time = null, string? subject = null, object? data = null)
        => _seeds.AppendEpicEventAsync(projectId, epicNumber, type, time, subject, data);

    protected Task SeedIssueEventHistoryAsync(string projectId, int issueNumber, int count) => _seeds.SeedIssueEventHistoryAsync(projectId, issueNumber, count);

    protected sealed record ProjectDto(string Id, string Name);

    protected sealed record ProjectEventResponseDto(
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
        int? EpicNumber,
        string? SessionSourceKind,
        string? WorkflowRunId,
        string? AgentId,
        string? AgentName);
}
