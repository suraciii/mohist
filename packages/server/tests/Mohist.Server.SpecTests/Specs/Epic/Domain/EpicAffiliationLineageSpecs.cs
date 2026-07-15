using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Epic.Domain;

[Collection("IntegrationWorkflow")]
public class EpicAffiliationLineageSpecs
{
    private readonly HttpClient _client;
    private readonly IGrainFactory _grains;
    private readonly IServiceProvider _services;

    public EpicAffiliationLineageSpecs(MohistIntegrationFixture fixture)
    {
        _client = fixture.Client;
        _grains = fixture.Grains;
        _services = fixture.Services;
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task LinkAndUnlink_SnapshotIssueAndWorkflowEpicLineageAtProductionTime()
    {
        var project = await CreateProjectAsync();
        var issue = await CreateIssueAsync(project.Id);
        var epic = await CreateEpicAsync(project.Id);
        using var scope = _services.CreateScope();
        var events = scope.ServiceProvider.GetRequiredService<IEventStore>();

        var created = Assert.Single(
            await events.ListIssueEventsAsync(issue.Id),
            entry => entry.Envelope.Type == EventCatalog.ReverseDns.IssueCreated);
        Assert.False(created.Envelope.Extensions.ContainsKey(EventCatalog.Lineage.EpicId));

        await _client.PostOkAsync(
            $"/api/projects/{project.Id}/epics/{epic.Id}/issues",
            new { issueId = issue.Id });

        var issueGrain = _grains.GetGrain<IIssueGrain>(issue.Id);
        var workflowRunId = await issueGrain.StartWorkAsync(
            new WorkflowProjectContext(project.Id, "Lineage snapshot", RepositoryBaseBranch: "main"));

        var workStarted = Assert.Single(
            await events.ListIssueEventsAsync(issue.Id),
            entry => entry.Envelope.Type == EventCatalog.ReverseDns.IssueWorkStarted);
        Assert.Equal(epic.Id, workStarted.Envelope.Extensions[EventCatalog.Lineage.EpicId]);

        var workflowStarted = Assert.Single(
            await events.ListAsync(workflowRunId),
            entry => entry.Envelope.Type == EventCatalog.ReverseDns.WorkflowRunStarted);
        Assert.Equal(epic.Id, workflowStarted.Envelope.Extensions[EventCatalog.Lineage.EpicId]);

        using (var unlink = await _client.DeleteAsync(
                   $"/api/projects/{project.Id}/epics/{epic.Id}/issues/{issue.Id}"))
        {
            Assert.Equal(HttpStatusCode.OK, unlink.StatusCode);
        }

        await issueGrain.CompleteWorkAsync(workflowRunId);

        var completed = Assert.Single(
            await events.ListIssueEventsAsync(issue.Id),
            entry => entry.Envelope.Type == EventCatalog.ReverseDns.IssueCompleted);
        Assert.False(completed.Envelope.Extensions.ContainsKey(EventCatalog.Lineage.EpicId));

        var reloadedCreated = Assert.Single(
            await events.ListIssueEventsAsync(issue.Id),
            entry => entry.Envelope.Type == EventCatalog.ReverseDns.IssueCreated);
        Assert.Equal(
            created.Envelope.Extensions.OrderBy(pair => pair.Key, StringComparer.Ordinal),
            reloadedCreated.Envelope.Extensions.OrderBy(pair => pair.Key, StringComparer.Ordinal));
        Assert.False(reloadedCreated.Envelope.Extensions.ContainsKey(EventCatalog.Lineage.EpicId));

        var workflowHistory = Assert.Single(
            await events.ListAsync(workflowRunId),
            entry => entry.Envelope.Type == EventCatalog.ReverseDns.WorkflowRunStarted);
        Assert.Equal(epic.Id, workflowHistory.Envelope.Extensions[EventCatalog.Lineage.EpicId]);
    }

    private async Task<ProjectDto> CreateProjectAsync()
    {
        var project = await _client.PostDataAsync<ProjectDto>(
            "/api/projects",
            new { name = $"lineage-{Guid.NewGuid():N}" });
        await _client.PostOkAsync(
            $"/api/projects/{project.Id}/repositories",
            new
            {
                name = "main",
                gitUrl = $"file://{Guid.NewGuid():N}",
                baseBranch = "main",
                isDefault = true,
            });
        return project;
    }

    private Task<IssueDto> CreateIssueAsync(string projectId) =>
        _client.PostDataAsync<IssueDto>(
            $"/api/projects/{projectId}/issues",
            new { title = "Lineage snapshot", projectId, isDraft = false });

    private Task<EpicDto> CreateEpicAsync(string projectId) =>
        _client.PostDataAsync<EpicDto>(
            $"/api/projects/{projectId}/epics",
            new { title = "Lineage snapshot", description = "lineage", priority = "p2", projectId });

    private sealed record ProjectDto(string Id);
    private sealed record IssueDto(string Id, int Number);
    private sealed record EpicDto(string Id);
}
