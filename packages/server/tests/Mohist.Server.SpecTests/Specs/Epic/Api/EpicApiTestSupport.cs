using Mohist.Server.Epic.Grains;
using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Orleans;
using System.Net;
using System.Net.Http.Json;
using Xunit;
namespace Mohist.Server.SpecTests.Specs.Epic.Api;

public abstract class EpicApiTestSupport
{
    protected readonly MohistIntegrationFixture _fixture;
    protected readonly HttpClient _client;
    protected readonly IGrainFactory _grains;

    protected EpicApiTestSupport(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
        _grains = fixture.Grains;
    }

    protected async Task StartEpicAsync(string projectId, EpicDto epic)
    {
        var grain = _grains.GetGrain<IEpicGrain>(GrainKey.Epic(new EpicKey(projectId, epic.Number)));
        await grain.StartAsync();
    }

    protected async Task AddOpenIssueAsync(string projectId, EpicDto epic)
    {
        var issue = await _client.PostDataAsync<IssueDto>(
            $"/api/projects/{projectId}/issues",
            new { title = "Open work", projectId, isDraft = false });
        await _client.PostOkAsync(
            $"/api/projects/{projectId}/epics/{epic.Number}/issues",
            new { issueNumber = issue.Number });
    }

    protected async Task CompleteIssueAsync(string projectId, IssueDto issueInfo)
    {
        var grain = _grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(projectId, issueInfo.Number)));
        var wrId = await grain.StartWorkAsync(new WorkflowProjectContext(projectId, "Epic API Test", RepositoryBaseBranch: "main"));
        await grain.CompleteWorkAsync(wrId);
    }

    protected Task DispatchPendingEventsAsync() =>
        _grains.GetGrain<IEventDispatcherGrain>(EventDispatcherGrain.Global).DispatchNowAsync();

    protected sealed record ProjectDto(string Id);
    protected sealed record EpicDto(int Number, string Title, string Description, string Priority, string Status, string CreatedAt, string UpdatedAt);
    protected sealed record EpicFullDto(int Number, string Title, string Description, string Priority, string Status, string CreatedAt, string UpdatedAt, string? PauseReason);
    protected sealed record EpicWithProgressDto(int Number, string Priority, string UpdatedAt);
    protected sealed record EpicWithProgressFullDto(int Number, string Status, string? PauseReason);
    protected sealed record EpicDetailDto(int Number, string Title, string Description, string Status, LinkedIssueDto[] LinkedIssues);
    protected sealed record EpicDetailFullDto(int Number, string Status, string? PauseReason, LinkedIssueDto[] LinkedIssues);
    protected sealed record LinkedIssueDto(int Number);
    protected sealed record IssueDto(int Number, IssueEpicDto? Epic);
    protected sealed record IssueEpicDto(int Number, string Title);
    protected sealed record NotFoundEnvelope(bool Success, string? Code = null, string? Error = null);
    protected sealed record ConflictEnvelope(bool Success, string? Code = null, string? Error = null, ConflictDetails? Details = null);
    protected sealed record ConflictDetails(string CurrentStatus, string? RequestedStatus = null);
}
