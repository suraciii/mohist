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
using Mohist.Server.Issue.Grains;
using Mohist.Server.Project.Services;
using Xunit;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.SpecTests.Specs.GitHub;

[Collection("GitHubFeed")]
public sealed class GitHubIssueMirrorSpecs
{
    private const string RepositoryName = "hello-world";
    private readonly GitHubFeedFixture _fixture;

    public GitHubIssueMirrorSpecs(GitHubFeedFixture fixture)
    {
        _fixture = fixture;
        fixture.Comments.Comments.Clear();
        fixture.Comments.CreatedIssues.Clear();
        fixture.Comments.UpdatedIssues.Clear();
        fixture.Comments.MarkerMatches.Clear();
        fixture.Comments.CreateFailure = null;
        fixture.Comments.FindFailure = null;
        fixture.Comments.CreateThenThrow = false;
        fixture.Comments.MarkerMatchCount = 0;
    }

    [Fact]
    public async Task ReadyIssue_CreatesMirrorWithMarkerAndConfirmation()
    {
        var (projectId, issueNumber, connectionId) = await CreateIssueAsync(isDraft: false);
        await PumpAsync();

        var created = Assert.Single(_fixture.Comments.CreatedIssues, x => x.ConnectionId == connectionId);
        Assert.Equal("Ready issue", created.Title);
        Assert.Contains(created.Marker, created.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("mohist:mirror", "Ready issue body", StringComparison.Ordinal);
        Assert.Single(_fixture.Comments.Comments, c =>
            c.ConnectionId == connectionId
            && c.GithubIssueNumber == created.GithubIssueNumber
            && c.Body.Contains($"Mohist issue #{issueNumber}", StringComparison.Ordinal));

        var link = await LoadLinkByIssueAsync(projectId, issueNumber);
        Assert.NotNull(link);
        Assert.Equal(created.GithubIssueNumber, link!.GithubIssueNumber);
        Assert.Equal(created.Marker, link.MirrorMarker);
        Assert.True(link.HasPostedComment(GitHubCommentKinds.MirrorCreated));
    }

    [Fact]
    public async Task DraftIssue_DoesNotCreateUntilMarkedReady()
    {
        var (projectId, issueNumber, _) = await CreateIssueAsync(isDraft: true);
        await PumpAsync();
        Assert.Empty(_fixture.Comments.CreatedIssues);
        Assert.Null(await LoadLinkByIssueAsync(projectId, issueNumber));

        await _fixture.Grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(projectId, issueNumber)))
            .UpdateFullAsync(new UpdateIssueData(
                IsDraft: false,
                PresentFields: new HashSet<string>([nameof(UpdateIssueData.IsDraft)], StringComparer.Ordinal)));
        await PumpAsync();

        Assert.Single(_fixture.Comments.CreatedIssues);
    }

    [Fact]
    public async Task UnknownCreateOutcome_ReconcilesByMarkerWithoutSecondPost()
    {
        var (projectId, issueNumber, connectionId) = await CreateIssueAsync(isDraft: false);
        _fixture.Comments.CreateThenThrow = true;
        await PumpAsync();

        var created = Assert.Single(_fixture.Comments.CreatedIssues);
        var pending = await LoadLinkByIssueAsync(projectId, issueNumber);
        Assert.NotNull(pending);
        Assert.True(pending!.IsPending);
        Assert.True(pending.MirrorCreateAttempted);

        _fixture.Comments.CreateThenThrow = false;
        _fixture.Comments.MarkerMatches.Enqueue(created.GithubIssueNumber);
        await DispatchIssueEventAsync(projectId, issueNumber, EventCatalog.ReverseDns.IssueContentChanged,
            new IssueContentChanged("Ready issue", "Ready issue body"));

        var linked = await LoadLinkByIssueAsync(projectId, issueNumber);
        Assert.Equal(created.GithubIssueNumber, linked!.GithubIssueNumber);
        Assert.Single(_fixture.Comments.Comments, c => c.ConnectionId == connectionId);
        Assert.Empty(_fixture.Comments.CreatedIssues.Skip(1));
    }

    [Fact]
    public async Task DuplicateMarker_ReconciliationFailsClosed()
    {
        var (projectId, issueNumber, _) = await CreateIssueAsync(isDraft: false);
        _fixture.Comments.CreateThenThrow = true;
        await PumpAsync();
        _fixture.Comments.CreateThenThrow = false;
        _fixture.Comments.MarkerMatchCount = 2;

        await DispatchIssueEventAsync(projectId, issueNumber, EventCatalog.ReverseDns.IssueContentChanged,
            new IssueContentChanged("Ready issue", "Ready issue body"));

        var link = await LoadLinkByIssueAsync(projectId, issueNumber);
        Assert.NotNull(link);
        Assert.True(link!.IsPending);
        Assert.Empty(_fixture.Comments.Comments);
    }

    [Fact]
    public async Task MohistContentChange_UpdatesMirrorWithMarker()
    {
        var (projectId, issueNumber, _) = await CreateIssueAsync(isDraft: false);
        await PumpAsync();
        _fixture.Comments.UpdatedIssues.Clear();

        await _fixture.Grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(projectId, issueNumber)))
            .UpdateAsync("Changed title", "Changed body");
        await PumpAsync();

        var updated = Assert.Single(_fixture.Comments.UpdatedIssues);
        Assert.Equal("Changed title", updated.Title);
        Assert.Contains("Changed body", updated.Body, StringComparison.Ordinal);
        Assert.Contains(updated.Marker, updated.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InboundMarkerOnlyEditIsEchoNoOp()
    {
        var (projectId, issueNumber, connectionId) = await CreateIssueAsync(isDraft: false);
        await PumpAsync();
        var link = await LoadLinkByIssueAsync(projectId, issueNumber);
        var before = await LoadIssueAsync(projectId, issueNumber);
        Assert.NotNull(link);
        Assert.NotNull(before);

        await DispatchGitHubEditAsync(connectionId, link!.GithubIssueNumber,
            before!.Title, GitHubMirrorMarker.Append(before.Body, link.MirrorMarker!));

        var after = await LoadIssueAsync(projectId, issueNumber);
        Assert.Equal(before.Title, after!.Title);
        Assert.Equal(before.Body, after.Body);
    }

    [Fact]
    public async Task InboundEditStripsMarkerAndPreservesWorkflowInput()
    {
        var (projectId, issueNumber, connectionId) = await CreateIssueAsync(isDraft: false);
        await PumpAsync();
        var link = await LoadLinkByIssueAsync(projectId, issueNumber);
        Assert.NotNull(link);

        await DispatchGitHubEditAsync(connectionId, link!.GithubIssueNumber,
            "GitHub title", $"GitHub body\n\n{link.MirrorMarker}");

        var issue = await LoadIssueAsync(projectId, issueNumber);
        Assert.Equal("GitHub title", issue!.Title);
        Assert.Equal("GitHub body", issue.Body);
        Assert.DoesNotContain(link.MirrorMarker!, issue.Body, StringComparison.Ordinal);
        var events = await _fixture.Services.GetRequiredService<IEventStore>()
            .ListIssueEventsAsync(projectId, issueNumber);
        Assert.Contains(events, e => e.Envelope.Type == EventCatalog.ReverseDns.IssueContentChanged
            && e.Envelope.Data?.GetProperty("source").GetString() == "github:alice");
    }

    private async Task<(string ProjectId, int IssueNumber, string ConnectionId)> CreateIssueAsync(bool isDraft)
    {
        var owner = $"octocat-{Guid.NewGuid():N}";
        var project = await _fixture.Client.CreateProjectWithDefaultRepositoryAsync<ProjectInfo>(
            "/api/projects", $"github-mirror-{Guid.NewGuid():N}", repoName: RepositoryName,
            gitUrl: $"https://github.com/{owner}/{RepositoryName}.git");
        var connection = await _fixture.Client.PostDataAsync<JsonElement>(
            $"/api/projects/{project.Id}/github-connections", new { owner, repo = RepositoryName });
        var issueNumber = await _fixture.Grains.GetGrain<IIssueCounterGrain>(GrainKey.IssueCounter(project.Id)).NextAsync();
        await _fixture.Grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(project.Id, issueNumber)))
            .CreateAsync(project.Id, issueNumber, "Ready issue", "Ready issue body", null, "p2", RepositoryName, isDraft: isDraft);
        return (project.Id, issueNumber, connection.GetProperty("id").GetString()!);
    }

    private async Task PumpAsync()
    {
        var dispatcher = _fixture.Services.GetRequiredService<IEventDispatcher>();
        await dispatcher.DrainAsync();
        await dispatcher.DrainAsync();
    }

    private async Task DispatchIssueEventAsync(string projectId, int issueNumber, string type, object data)
    {
        var issue = await LoadIssueAsync(projectId, issueNumber);
        var evt = new CloudEvent(
            $"mirror-{Guid.NewGuid():N}",
            new Uri($"/mohist/projects/{projectId}/issues/{issueNumber}", UriKind.Relative),
            type,
            _fixture.TimeProvider.GetUtcNow(),
            JsonSerializer.SerializeToElement(data, CloudEvent.JsonOptions),
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
        var evt = new CloudEvent(
            $"github-edit-{Guid.NewGuid():N}",
            new Uri($"/mohist/projects/{await ProjectForConnectionAsync(connectionId)}/github-connections/{connectionId}", UriKind.Relative),
            EventCatalog.ReverseDns.GitHubIssuesEdited,
            _fixture.TimeProvider.GetUtcNow(),
            JsonSerializer.SerializeToElement(new
            {
                issue = new { number = githubIssueNumber, title, body, labels = Array.Empty<object>() },
                sender = new { login = "alice" },
            }, CloudEvent.JsonOptions),
            extensions: new Dictionary<string, string>
            {
                [EventCatalog.Lineage.ProjectId] = await ProjectForConnectionAsync(connectionId),
            });
        await _fixture.Services.GetRequiredService<IEventStore>().AppendAsync(evt);
        await PumpAsync();
    }

    private async Task<string> ProjectForConnectionAsync(string connectionId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using var context = await db.CreateDbContextAsync();
        return (await context.GitHubConnections.AsNoTracking().SingleAsync(x => x.Id == connectionId)).ProjectId;
    }

    private async Task<GitHubIssueLink?> LoadLinkByIssueAsync(string projectId, int issueNumber)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<GitHubIssueLinkStore>()
            .GetByIssueAsync(projectId, issueNumber);
    }

    private async Task<DomainIssue?> LoadIssueAsync(string projectId, int issueNumber)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IIssueStore>()
            .LoadAsync(GrainKey.Issue(new IssueKey(projectId, issueNumber)));
    }
}
