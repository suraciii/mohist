using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.GitHub.Domain;
using Mohist.Server.GitHub.Infrastructure;
using Mohist.Server.GitHub.Ports;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Domain.Events;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Project.Domain;
using Mohist.Server.Project.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.GitHub;

[Collection("GitHubFeed")]
public sealed class GitHubSyncSpecs
{
    private const string RepositoryName = "hello-world";
    private readonly GitHubFeedFixture _fixture;

    public GitHubSyncSpecs(GitHubFeedFixture fixture)
    {
        _fixture = fixture;
        fixture.Comments.Comments.Clear();
        fixture.Comments.CreatedIssues.Clear();
        fixture.Comments.UpdatedIssues.Clear();
        fixture.Comments.MarkerMatches.Clear();
        fixture.Comments.CreateFailure = null;
        fixture.Comments.FindFailure = null;
        fixture.Comments.ConfirmationFailure = null;
        fixture.Comments.UpdateFailure = null;
        fixture.Comments.CreateThenThrow = false;
        fixture.Comments.MarkerMatchCount = 0;
        fixture.Comments.Issues.Clear();
    }

    [Fact]
    public async Task SyncCreatesMissingMirrorWithoutDuplicating()
    {
        var owner = $"octocat-{Guid.NewGuid():N}";
        var project = await _fixture.Client.CreateProjectWithDefaultRepositoryAsync<ProjectInfo>(
            "/api/projects", $"github-sync-create-{Guid.NewGuid():N}", repoName: RepositoryName,
            gitUrl: $"https://github.com/{owner}/{RepositoryName}.git");
        var issueNumber = await CreateIssueInProjectAsync(project.Id, isDraft: false);
        await PumpAsync();

        await _fixture.Client.PostDataAsync<JsonElement>(
            $"/api/projects/{project.Id}/github-connections", new { owner, repo = RepositoryName });

        using var first = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/issues/{issueNumber}/github/sync", new { });
        first.EnsureSuccessStatusCode();
        using var second = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/issues/{issueNumber}/github/sync", new { });
        second.EnsureSuccessStatusCode();

        Assert.Single(_fixture.Comments.CreatedIssues);
        var link = await LoadLinkAsync(project.Id, issueNumber);
        Assert.NotNull(link);
        Assert.False(link!.IsPending);
    }

    [Fact]
    public async Task SyncClearsRecordedErrorAndProjectsCurrentContent()
    {
        var (projectId, issueNumber, connectionId) = await CreateMirroredIssueAsync();
        _fixture.Comments.UpdateFailure = new InvalidOperationException("GitHub update unavailable");
        await DispatchContentChangeAsync(projectId, issueNumber);

        var failed = await LoadLinkAsync(projectId, issueNumber);
        Assert.Equal(GitHubSyncStatus.Error, failed!.SyncStatus);
        Assert.Contains("unavailable", failed.LastError!.Detail, StringComparison.Ordinal);
        Assert.Equal(connectionId, failed.ProjectId == projectId ? connectionId : string.Empty);

        _fixture.Comments.UpdateFailure = null;
        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/github/sync", new { });
        response.EnsureSuccessStatusCode();

        var recovered = await LoadLinkAsync(projectId, issueNumber);
        Assert.Equal(GitHubSyncStatus.Healthy, recovered!.SyncStatus);
        Assert.Null(recovered.LastError);
        Assert.Contains(_fixture.Comments.UpdatedIssues, update => update.GithubIssueNumber == recovered.GithubIssueNumber);
    }

    [Fact]
    public async Task LinkOverwritesGitHubFromMohistAndUnlinkPreservesBothSides()
    {
        var owner = $"octocat-{Guid.NewGuid():N}";
        var project = await _fixture.Client.CreateProjectWithDefaultRepositoryAsync<ProjectInfo>(
            "/api/projects", $"github-link-{Guid.NewGuid():N}", repoName: RepositoryName,
            gitUrl: $"https://github.com/{owner}/{RepositoryName}.git");
        var issueNumber = await CreateIssueInProjectAsync(project.Id, isDraft: true);
        await _fixture.Client.PostDataAsync<JsonElement>(
            $"/api/projects/{project.Id}/github-connections", new { owner, repo = RepositoryName });

        const int githubIssueNumber = 817;
        _fixture.Comments.Issues[githubIssueNumber] = new GitHubIssueSnapshot(
            githubIssueNumber, "Existing GitHub title", "Existing GitHub body");
        using var linkResponse = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/issues/{issueNumber}/github/link",
            new { repository = $"{owner}/{RepositoryName}", number = githubIssueNumber });
        linkResponse.EnsureSuccessStatusCode();

        var link = await LoadLinkAsync(project.Id, issueNumber);
        Assert.Equal(githubIssueNumber, link!.GithubIssueNumber);
        var update = Assert.Single(_fixture.Comments.UpdatedIssues, item => item.GithubIssueNumber == githubIssueNumber);
        Assert.Equal("Ready issue", update.Title);
        Assert.Contains("Ready issue body", update.Body, StringComparison.Ordinal);

        using var unlinkResponse = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/issues/{issueNumber}/github/unlink", new { });
        unlinkResponse.EnsureSuccessStatusCode();
        Assert.Null(await LoadLinkAsync(project.Id, issueNumber));
        Assert.Equal("Ready issue", _fixture.Comments.Issues[githubIssueNumber].Title);
        Assert.Contains("Ready issue body", _fixture.Comments.Issues[githubIssueNumber].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisabledConnectionPausesInboundAndEnableReprojectsOnce()
    {
        var (projectId, issueNumber, connectionId) = await CreateMirroredIssueAsync();
        var link = await LoadLinkAsync(projectId, issueNumber);
        Assert.NotNull(link);

        using var disabled = await _fixture.Client.PostAsync(
            $"/api/projects/{projectId}/github-connections/{connectionId}/disable", JsonContent.Create(new { }));
        disabled.EnsureSuccessStatusCode();
        await DispatchGitHubEditAsync(connectionId, link!.GithubIssueNumber, "Ignored while disabled", "Ignored body");
        var pausedIssue = await LoadIssueAsync(projectId, issueNumber);
        Assert.Equal("Ready issue", pausedIssue!.Title);

        _fixture.Comments.UpdatedIssues.Clear();
        using var enabled = await _fixture.Client.PostAsync(
            $"/api/projects/{projectId}/github-connections/{connectionId}/enable", JsonContent.Create(new { }));
        enabled.EnsureSuccessStatusCode();
        var projection = Assert.Single(_fixture.Comments.UpdatedIssues);
        Assert.Equal("Ready issue", projection.Title);
        Assert.Contains("Ready issue body", projection.Body, StringComparison.Ordinal);
    }

    private async Task<(string ProjectId, int IssueNumber, string ConnectionId)> CreateMirroredIssueAsync()
    {
        var owner = $"octocat-{Guid.NewGuid():N}";
        var project = await _fixture.Client.CreateProjectWithDefaultRepositoryAsync<ProjectInfo>(
            "/api/projects", $"github-sync-{Guid.NewGuid():N}", repoName: RepositoryName,
            gitUrl: $"https://github.com/{owner}/{RepositoryName}.git");
        var connection = await _fixture.Client.PostDataAsync<JsonElement>(
            $"/api/projects/{project.Id}/github-connections", new { owner, repo = RepositoryName });
        var issueNumber = await CreateIssueInProjectAsync(project.Id, isDraft: false);
        await PumpAsync();
        return (project.Id, issueNumber, connection.GetProperty("id").GetString()!);
    }

    private async Task<int> CreateIssueInProjectAsync(string projectId, bool isDraft)
    {
        var issueNumber = await _fixture.Grains.GetGrain<IIssueCounterGrain>(GrainKey.IssueCounter(projectId)).NextAsync();
        await _fixture.Grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(projectId, issueNumber)))
            .CreateAsync(projectId, issueNumber, "Ready issue", "Ready issue body", null, "p2", RepositoryName, isDraft: isDraft);
        return issueNumber;
    }

    private async Task DispatchContentChangeAsync(string projectId, int issueNumber)
    {
        var evt = new CloudEvent(
            $"sync-content-{Guid.NewGuid():N}",
            new Uri($"/mohist/projects/{projectId}/issues/{issueNumber}", UriKind.Relative),
            EventCatalog.ReverseDns.IssueContentChanged,
            _fixture.TimeProvider.GetUtcNow(),
            JsonSerializer.SerializeToElement(new IssueContentChanged("Changed title", "Changed body"), CloudEvent.JsonOptions),
            extensions: new Dictionary<string, string>
            {
                [EventCatalog.Lineage.ProjectId] = projectId,
                [EventCatalog.Lineage.Issue] = issueNumber.ToString(),
            });
        await _fixture.Services.GetRequiredService<IEventStore>().AppendAsync(evt);
        await PumpAsync();
    }

    private async Task DispatchGitHubEditAsync(string connectionId, int githubIssueNumber, string title, string body)
    {
        var projectId = await ProjectForConnectionAsync(connectionId);
        var evt = new CloudEvent(
            $"github-edit-{Guid.NewGuid():N}",
            new Uri($"/mohist/projects/{projectId}/github-connections/{connectionId}", UriKind.Relative),
            EventCatalog.ReverseDns.GitHubIssuesEdited,
            _fixture.TimeProvider.GetUtcNow(),
            JsonSerializer.SerializeToElement(new
            {
                issue = new { number = githubIssueNumber, title, body, labels = Array.Empty<object>() },
                sender = new { login = "alice" },
            }, CloudEvent.JsonOptions),
            extensions: new Dictionary<string, string>
            {
                [EventCatalog.Lineage.ProjectId] = projectId,
            });
        await _fixture.Services.GetRequiredService<IEventStore>().AppendAsync(evt);
        await PumpAsync();
    }

    private async Task<GitHubIssueLink?> LoadLinkAsync(string projectId, int issueNumber) =>
        await _fixture.Services.GetRequiredService<GitHubIssueLinkStore>().GetByIssueAsync(projectId, issueNumber);

    private async Task<DomainIssue?> LoadIssueAsync(string projectId, int issueNumber) =>
        await _fixture.Services.GetRequiredService<IIssueStore>()
            .LoadAsync(GrainKey.Issue(new IssueKey(projectId, issueNumber)));

    private async Task<string> ProjectForConnectionAsync(string connectionId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        return (await db.GitHubConnections.AsNoTracking().SingleAsync(row => row.Id == connectionId)).ProjectId;
    }

    private async Task PumpAsync()
    {
        var dispatcher = _fixture.Services.GetRequiredService<IEventDispatcher>();
        await dispatcher.DrainAsync();
        await dispatcher.DrainAsync();
    }

}
