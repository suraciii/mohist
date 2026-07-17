using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Epic.Domain;

[Collection("IntegrationWorkflow")]
public class EpicAffiliationLineageSpecs
{
    private readonly HttpClient _client;
    private readonly IServiceProvider _services;

    public EpicAffiliationLineageSpecs(MohistIntegrationFixture fixture)
    {
        _client = fixture.Client;
        _services = fixture.Services;
    }

    [Fact]
    public async Task LinkAndUnlink_PersistIssueOwnedAffiliationEvents()
    {
        var project = await CreateProjectAsync();
        var issue = await CreateIssueAsync(project.Id);
        var epic = await CreateEpicAsync(project.Id);
        using var scope = _services.CreateScope();
        var events = scope.ServiceProvider.GetRequiredService<IEventStore>();

        var created = Assert.Single(
            await events.ListIssueEventsAsync(project.Id, issue.Number, 200),
            entry => entry.Envelope.Type == EventCatalog.ReverseDns.IssueCreated);
        Assert.False(created.Envelope.Extensions.ContainsKey(EventCatalog.Lineage.Epic));

        await _client.PostOkAsync(
            $"/api/projects/{project.Id}/epics/{epic.Number}/issues",
            new { issueNumber = issue.Number });

        var linked = Assert.Single(
            await events.ListIssueEventsAsync(project.Id, issue.Number, 200),
            entry => entry.Envelope.Type == EventCatalog.ReverseDns.IssueEpicChanged);
        Assert.Equal(project.Id, linked.Envelope.Extensions[EventCatalog.Lineage.ProjectId]);
        Assert.Equal(issue.Number.ToString(), linked.Envelope.Extensions[EventCatalog.Lineage.Issue]);
        Assert.Equal(epic.Number.ToString(), linked.Envelope.Extensions[EventCatalog.Lineage.Epic]);

        using (var unlink = await _client.DeleteAsync(
                   $"/api/projects/{project.Id}/epics/{epic.Number}/issues/{issue.Number}"))
        {
            unlink.EnsureSuccessStatusCode();
        }

        var changes = (await events.ListIssueEventsAsync(project.Id, issue.Number, 200))
            .Where(entry => entry.Envelope.Type == EventCatalog.ReverseDns.IssueEpicChanged)
            .ToList();
        Assert.Equal(2, changes.Count);
        Assert.False(changes[1].Envelope.Extensions.ContainsKey(EventCatalog.Lineage.Epic));
    }

    private async Task<ProjectDto> CreateProjectAsync() =>
        await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>(
            "/api/projects",
            $"lineage-{Guid.NewGuid():N}",
            repoName: "main",
            gitUrl: $"file://{Guid.NewGuid():N}");

    private Task<IssueDto> CreateIssueAsync(string projectId) =>
        _client.PostDataAsync<IssueDto>(
            $"/api/projects/{projectId}/issues",
            new { title = "Lineage snapshot", projectId, isDraft = false });

    private Task<EpicDto> CreateEpicAsync(string projectId) =>
        _client.PostDataAsync<EpicDto>(
            $"/api/projects/{projectId}/epics",
            new { title = "Lineage snapshot", description = "lineage", priority = "p2", projectId });

    private sealed record ProjectDto(string Id);
    private sealed record IssueDto(int Number);
    private sealed record EpicDto(int Number);
}
