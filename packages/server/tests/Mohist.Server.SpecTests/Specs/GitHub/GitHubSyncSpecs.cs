using System.Net;
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

[Collection("GitHubCommand")]
public sealed partial class GitHubSyncSpecs
{
    private const string RepositoryName = "hello-world";
    private readonly GitHubCommandFixture _fixture;

    public GitHubSyncSpecs(GitHubCommandFixture fixture)
    {
        _fixture = fixture;
        fixture.Comments.Comments.Clear();
        fixture.Comments.CreatedIssues.Clear();
        fixture.Comments.UpdatedIssues.Clear();
        fixture.Comments.StateLabels.Clear();
        fixture.Comments.Closes.Clear();
        fixture.Comments.MarkerMatches.Clear();
        fixture.Comments.CreateFailure = null;
        fixture.Comments.FindFailure = null;
        fixture.Comments.ConfirmationFailure = null;
        fixture.Comments.PostFailure = null;
        fixture.Comments.PostThenThrow = false;
        fixture.Comments.PostEntered = null;
        fixture.Comments.ReleasePost = null;
        fixture.Comments.FindEntered = null;
        fixture.Comments.ReleaseFind = null;
        fixture.Comments.UpdateFailure = null;
        fixture.Comments.UpdateFailures.Clear();
        fixture.Comments.LabelFailure = null;
        fixture.Comments.CloseFailure = null;
        fixture.Comments.CloseThenThrow = false;
        fixture.Comments.CloseEntered = null;
        fixture.Comments.ReleaseClose = null;
        fixture.Comments.CreateThenThrow = false;
        fixture.Comments.MarkerMatchCount = 0;
        fixture.Comments.CreateIssueNumberOverride = null;
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
    public async Task SyncRecreatesMirrorWhenRemoteMirrorReturnsNotFound()
    {
        var (projectId, issueNumber, _) = await CreateMirroredIssueAsync();
        var original = await LoadLinkAsync(projectId, issueNumber);
        Assert.NotNull(original);
        var originalNumber = original!.GithubIssueNumber;
        _fixture.Comments.UpdateFailures.Enqueue(new HttpRequestException("mirror deleted", null, System.Net.HttpStatusCode.NotFound));

        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/github/sync", new { });
        response.EnsureSuccessStatusCode();

        var recreated = await LoadLinkAsync(projectId, issueNumber);
        Assert.NotNull(recreated);
        Assert.NotEqual(originalNumber, recreated!.GithubIssueNumber);
        Assert.False(recreated.IsPending);
        Assert.Contains(_fixture.Comments.CreatedIssues, created => created.GithubIssueNumber == recreated.GithubIssueNumber);
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
    public async Task DefiniteCreateFailure_ReleasesReservationForLaterSync()
    {
        var owner = $"octocat-{Guid.NewGuid():N}";
        var project = await _fixture.Client.CreateProjectWithDefaultRepositoryAsync<ProjectInfo>(
            "/api/projects", $"github-sync-create-retry-{Guid.NewGuid():N}", repoName: RepositoryName,
            gitUrl: $"https://github.com/{owner}/{RepositoryName}.git");
        var connection = await _fixture.Client.PostDataAsync<JsonElement>(
            $"/api/projects/{project.Id}/github-connections",
            new { owner, repo = RepositoryName });
        _fixture.Comments.CreateFailure = new InvalidOperationException("GitHub rejected issue creation");
        var issueNumber = await CreateIssueInProjectAsync(project.Id, isDraft: false);
        await PumpAsync();

        var failed = await LoadLinkAsync(project.Id, issueNumber);
        Assert.NotNull(failed);
        Assert.True(failed!.IsPending);
        Assert.False(failed.MirrorCreateAttempted);
        Assert.Equal(GitHubSyncStatus.Error, failed.SyncStatus);

        _fixture.Comments.CreateFailure = null;
        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/issues/{issueNumber}/github/sync", new { });
        response.EnsureSuccessStatusCode();

        var linked = await LoadLinkAsync(project.Id, issueNumber);
        Assert.NotNull(linked);
        Assert.False(linked!.IsPending);
        Assert.Single(_fixture.Comments.CreatedIssues,
            created => created.ConnectionId == connection.GetProperty("id").GetString());
    }

    [Fact]
    public async Task InvalidCreateIdentity_LeavesIntentUnresolvedWithoutSecondPost()
    {
        var owner = $"octocat-{Guid.NewGuid():N}";
        var project = await _fixture.Client.CreateProjectWithDefaultRepositoryAsync<ProjectInfo>(
            "/api/projects", $"github-sync-invalid-create-{Guid.NewGuid():N}", repoName: RepositoryName,
            gitUrl: $"https://github.com/{owner}/{RepositoryName}.git");
        await _fixture.Client.PostDataAsync<JsonElement>(
            $"/api/projects/{project.Id}/github-connections",
            new { owner, repo = RepositoryName });
        _fixture.Comments.CreateIssueNumberOverride = 0;
        var issueNumber = await CreateIssueInProjectAsync(project.Id, isDraft: false);
        await PumpAsync();

        var pending = await LoadLinkAsync(project.Id, issueNumber);
        Assert.True(pending!.IsPending);
        Assert.True(pending.MirrorCreateAttempted);
        Assert.Single(_fixture.Comments.CreatedIssues);

        _fixture.Comments.CreateIssueNumberOverride = null;
        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/issues/{issueNumber}/github/sync", new { });
        Assert.Equal(System.Net.HttpStatusCode.Conflict, response.StatusCode);
        Assert.Single(_fixture.Comments.CreatedIssues);
    }

    [Fact]
    public async Task SyncReprojectsCurrentDoneStateBeforeClearingError()
    {
        var (projectId, issueNumber, _) = await CreateMirroredIssueAsync();
        var grain = _fixture.Grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(projectId, issueNumber)));
        await grain.UpdateFullAsync(new UpdateIssueData(
            NoWorkflow: true,
            PresentFields: new HashSet<string>([nameof(UpdateIssueData.NoWorkflow)], StringComparer.Ordinal)));
        await grain.StartWorkAsync();
        await PumpAsync();
        await grain.MarkDoneAsync();
        await PumpAsync();

        await ClearProjectionBookkeepingAsync(projectId, issueNumber);
        _fixture.Comments.Comments.Clear();
        _fixture.Comments.UpdatedIssues.Clear();
        _fixture.Comments.StateLabels.Clear();
        _fixture.Comments.Closes.Clear();

        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/github/sync", new { });
        response.EnsureSuccessStatusCode();

        var link = await LoadLinkAsync(projectId, issueNumber);
        Assert.Equal(GitHubSyncStatus.Healthy, link!.SyncStatus);
        Assert.Contains(GitHubStateLabels.Done, _fixture.Comments.StateLabels.Select(label => label.StateLabel));
        Assert.Contains(_fixture.Comments.Comments, comment =>
            comment.Body.Contains("已完成该需求", StringComparison.Ordinal));
        var close = Assert.Single(_fixture.Comments.Closes);
        Assert.Equal("completed", close.StateReason);
    }

    [Fact]
    public async Task SyncReprojectsCurrentCancelledStateBeforeClearingError()
    {
        var (projectId, issueNumber, _) = await CreateMirroredIssueAsync();
        var grain = _fixture.Grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(projectId, issueNumber)));
        await grain.UpdateFullAsync(new UpdateIssueData(
            NoWorkflow: true,
            PresentFields: new HashSet<string>([nameof(UpdateIssueData.NoWorkflow)], StringComparer.Ordinal)));
        await grain.StartWorkAsync();
        await PumpAsync();
        await grain.CancelAsync();
        await PumpAsync();

        await ClearProjectionBookkeepingAsync(projectId, issueNumber);
        _fixture.Comments.Comments.Clear();
        _fixture.Comments.UpdatedIssues.Clear();
        _fixture.Comments.StateLabels.Clear();
        _fixture.Comments.Closes.Clear();

        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/github/sync", new { });
        response.EnsureSuccessStatusCode();

        var link = await LoadLinkAsync(projectId, issueNumber);
        Assert.Equal(GitHubSyncStatus.Healthy, link!.SyncStatus);
        Assert.Contains(_fixture.Comments.Comments, comment =>
            comment.Body.Contains("已取消该需求", StringComparison.Ordinal));
        var close = Assert.Single(_fixture.Comments.Closes);
        Assert.Equal("not_planned", close.StateReason);
    }

    [Fact]
    public async Task Label404_DoesNotRecreateTheMirror()
    {
        var (projectId, issueNumber, _) = await CreateMirroredIssueAsync();
        var grain = _fixture.Grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(projectId, issueNumber)));
        await grain.UpdateFullAsync(new UpdateIssueData(
            NoWorkflow: true,
            PresentFields: new HashSet<string>([nameof(UpdateIssueData.NoWorkflow)], StringComparer.Ordinal)));
        await grain.StartWorkAsync();
        await PumpAsync();
        var original = await LoadLinkAsync(projectId, issueNumber);
        Assert.NotNull(original);
        await ClearProjectionBookkeepingAsync(projectId, issueNumber);

        _fixture.Comments.LabelFailure = new HttpRequestException(
            "label endpoint returned not found",
            null,
            System.Net.HttpStatusCode.NotFound);
        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/github/sync", new { });
        Assert.Equal(System.Net.HttpStatusCode.Conflict, response.StatusCode);

        var current = await LoadLinkAsync(projectId, issueNumber);
        Assert.Equal(original!.GithubIssueNumber, current!.GithubIssueNumber);
        Assert.Single(_fixture.Comments.CreatedIssues);
    }

    [Fact]
    public async Task Close404_DoesNotRecreateTheMirror()
    {
        var (projectId, issueNumber, _) = await CreateMirroredIssueAsync();
        var grain = _fixture.Grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(projectId, issueNumber)));
        await grain.UpdateFullAsync(new UpdateIssueData(
            NoWorkflow: true,
            PresentFields: new HashSet<string>([nameof(UpdateIssueData.NoWorkflow)], StringComparer.Ordinal)));
        await grain.StartWorkAsync();
        await PumpAsync();
        await grain.MarkDoneAsync();
        await PumpAsync();
        var original = await LoadLinkAsync(projectId, issueNumber);
        Assert.NotNull(original);
        await ClearProjectionBookkeepingAsync(projectId, issueNumber);

        _fixture.Comments.CloseFailure = new HttpRequestException(
            "close endpoint returned not found",
            null,
            System.Net.HttpStatusCode.NotFound);
        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/github/sync", new { });
        Assert.Equal(System.Net.HttpStatusCode.Conflict, response.StatusCode);

        var current = await LoadLinkAsync(projectId, issueNumber);
        Assert.Equal(original!.GithubIssueNumber, current!.GithubIssueNumber);
        Assert.Single(_fixture.Comments.CreatedIssues);
    }

    [Fact]
    public async Task UnknownCommentOutcome_IsReconciledByHostedRecoveryWithoutDuplicate()
    {
        var (projectId, issueNumber, _) = await CreateMirroredIssueAsync();
        await ClearProjectionBookkeepingAsync(projectId, issueNumber);
        _fixture.Comments.Comments.Clear();
        _fixture.Comments.PostThenThrow = true;

        await DispatchContentChangeAsync(projectId, issueNumber);

        var link = await LoadLinkAsync(projectId, issueNumber);
        Assert.NotNull(link);
        Assert.False(link!.HasPostedComment(GitHubCommentKinds.MirrorCreated));
        Assert.Single(_fixture.Comments.Comments);

        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(5));
        var worker = _fixture.Services.GetRequiredService<GitHubIssueCommentOperationRecoveryWorker>();
        Assert.True(await worker.ProcessPendingAsync() >= 1);

        link = await LoadLinkAsync(projectId, issueNumber);
        Assert.True(link!.HasPostedComment(GitHubCommentKinds.MirrorCreated));
        Assert.Single(_fixture.Comments.Comments,
            comment => comment.GithubIssueNumber == link.GithubIssueNumber);
    }

    [Fact]
    public async Task CredentialFailureRetainsCommentReservationUntilReconnection()
    {
        var (projectId, issueNumber, connectionId) = await CreateMirroredIssueAsync();
        await ClearProjectionBookkeepingAsync(projectId, issueNumber);
        _fixture.Comments.Comments.Clear();
        _fixture.Comments.ConfirmationFailure = new GitHubRemoteRequestException(
            "GitHub permission denied",
            HttpStatusCode.Forbidden);

        await DispatchContentChangeAsync(projectId, issueNumber);

        var link = (await LoadLinkAsync(projectId, issueNumber))!;
        Assert.Equal(
            GitHubCommentOperationStatus.Reserved,
            await LoadOperationStatusAsync(link.Id, GitHubCommentKinds.MirrorCreated));

        using (var disabled = await _fixture.Client.PostAsync(
            $"/api/projects/{projectId}/github-connections/{connectionId}/disable", JsonContent.Create(new { })))
            disabled.EnsureSuccessStatusCode();
        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(5));
        var worker = _fixture.Services.GetRequiredService<GitHubIssueCommentOperationRecoveryWorker>();
        Assert.Equal(0, await worker.ProcessPendingAsync());
        Assert.Equal(
            GitHubCommentOperationStatus.Reserved,
            await LoadOperationStatusAsync(link.Id, GitHubCommentKinds.MirrorCreated));

        _fixture.Comments.ConfirmationFailure = null;
        using (var enabled = await _fixture.Client.PostAsync(
            $"/api/projects/{projectId}/github-connections/{connectionId}/enable", JsonContent.Create(new { })))
            enabled.EnsureSuccessStatusCode();
        Assert.True(await worker.ProcessPendingAsync() >= 1);
        Assert.Equal(
            GitHubCommentOperationStatus.Posted,
            await LoadOperationStatusAsync(link.Id, GitHubCommentKinds.MirrorCreated));
    }

    [Fact]
    public async Task DisabledConnectionRetainsUnknownCommentUntilEnabled()
    {
        var (projectId, issueNumber, connectionId) = await CreateMirroredIssueAsync();
        var link = (await LoadLinkAsync(projectId, issueNumber))!;
        const string commentKey = "disabled-comment";
        await ReserveAndDeferOperationAsync(
            link,
            commentKey,
            GitHubCommentOperationKind.Comment,
            "comment retained while disabled",
            stateReason: null);

        using (var disabled = await _fixture.Client.PostAsync(
            $"/api/projects/{projectId}/github-connections/{connectionId}/disable", JsonContent.Create(new { })))
            disabled.EnsureSuccessStatusCode();

        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(5));
        var marker = GitHubCommentOperationMarker.For(link.Id, commentKey);
        var markerCountBeforeRecovery = _fixture.Comments.Comments.Count(comment =>
            comment.Body.Contains(marker, StringComparison.Ordinal));
        var worker = _fixture.Services.GetRequiredService<GitHubIssueCommentOperationRecoveryWorker>();
        await worker.ProcessPendingAsync();
        Assert.Equal(markerCountBeforeRecovery, _fixture.Comments.Comments.Count(comment =>
            comment.Body.Contains(marker, StringComparison.Ordinal)));
        Assert.Equal(
            GitHubCommentOperationStatus.Reserved,
            await LoadOperationStatusAsync(link.Id, commentKey));

        using (var enabled = await _fixture.Client.PostAsync(
            $"/api/projects/{projectId}/github-connections/{connectionId}/enable", JsonContent.Create(new { })))
            enabled.EnsureSuccessStatusCode();

        Assert.True(await worker.ProcessPendingAsync() >= 1);
        var recovered = await LoadLinkAsync(projectId, issueNumber);
        Assert.True(recovered!.HasPostedComment(commentKey));
        Assert.Equal(1, _fixture.Comments.Comments.Count(comment =>
            comment.Body.Contains(marker, StringComparison.Ordinal)));
        Assert.Equal(
            GitHubCommentOperationStatus.Posted,
            await LoadOperationStatusAsync(link.Id, commentKey));
        await worker.ProcessPendingAsync();
        Assert.Equal(1, _fixture.Comments.Comments.Count(comment =>
            comment.Body.Contains(marker, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task DisabledConnectionRetainsUnknownCloseUntilEnabled()
    {
        var (projectId, issueNumber, connectionId) = await CreateMirroredIssueAsync();
        var link = (await LoadLinkAsync(projectId, issueNumber))!;
        const string closeKey = GitHubCommentKinds.ClosedCompleted;
        await ReserveAndDeferOperationAsync(
            link,
            closeKey,
            GitHubCommentOperationKind.Close,
            body: null,
            stateReason: "completed");

        using (var disabled = await _fixture.Client.PostAsync(
            $"/api/projects/{projectId}/github-connections/{connectionId}/disable", JsonContent.Create(new { })))
            disabled.EnsureSuccessStatusCode();

        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(5));
        var closesBeforeRecovery = _fixture.Comments.Closes.Count(close =>
            close.GithubIssueNumber == link.GithubIssueNumber
                && close.StateReason == "completed");
        var worker = _fixture.Services.GetRequiredService<GitHubIssueCommentOperationRecoveryWorker>();
        await worker.ProcessPendingAsync();
        Assert.Equal(closesBeforeRecovery, _fixture.Comments.Closes.Count(close =>
            close.GithubIssueNumber == link.GithubIssueNumber
                && close.StateReason == "completed"));
        Assert.Equal(
            GitHubCommentOperationStatus.Reserved,
            await LoadOperationStatusAsync(link.Id, closeKey));

        using (var enabled = await _fixture.Client.PostAsync(
            $"/api/projects/{projectId}/github-connections/{connectionId}/enable", JsonContent.Create(new { })))
            enabled.EnsureSuccessStatusCode();

        Assert.True(await worker.ProcessPendingAsync() >= 1);
        var recovered = await LoadLinkAsync(projectId, issueNumber);
        Assert.True(recovered!.HasPostedComment(closeKey));
        Assert.Equal(1, _fixture.Comments.Closes.Count(close =>
            close.GithubIssueNumber == link.GithubIssueNumber
                && close.StateReason == "completed"));
        Assert.Equal(
            GitHubCommentOperationStatus.Posted,
            await LoadOperationStatusAsync(link.Id, closeKey));
        await worker.ProcessPendingAsync();
        Assert.Equal(1, _fixture.Comments.Closes.Count(close =>
            close.GithubIssueNumber == link.GithubIssueNumber
                && close.StateReason == "completed"));
    }

    [Fact]
    public async Task RecoveryCommentCompletionAfterMirrorResetDoesNotRecordStaleBookkeeping()
    {
        var (projectId, issueNumber, _) = await CreateMirroredIssueAsync();
        var link = (await LoadLinkAsync(projectId, issueNumber))!;
        const string commentKey = "stale-comment";
        await ReserveAndDeferOperationAsync(
            link,
            commentKey,
            GitHubCommentOperationKind.Comment,
            "stale comment",
            stateReason: null);

        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(5));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _fixture.Comments.PostEntered = entered;
        _fixture.Comments.ReleasePost = release;
        var worker = _fixture.Services.GetRequiredService<GitHubIssueCommentOperationRecoveryWorker>();
        var recovery = worker.ProcessPendingAsync();
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var newGithubIssueNumber = link.GithubIssueNumber + 1000;
        try
        {
            await ResetMirrorAsync(link.Id, link.GithubIssueNumber);
            await SetMirrorAsync(link.Id, newGithubIssueNumber);
        }
        finally
        {
            release.TrySetResult();
        }
        await recovery;

        var current = await LoadLinkAsync(projectId, issueNumber);
        Assert.Equal(newGithubIssueNumber, current!.GithubIssueNumber);
        Assert.False(current.HasPostedComment(commentKey));
        Assert.Null(await LoadOperationStatusAsync(link.Id, commentKey));
    }

    [Fact]
    public async Task RecoveryCloseCompletionAfterMirrorResetDoesNotRecordStaleBookkeeping()
    {
        var (projectId, issueNumber, _) = await CreateMirroredIssueAsync();
        var link = (await LoadLinkAsync(projectId, issueNumber))!;
        const string closeKey = "stale-close";
        await ReserveAndDeferOperationAsync(
            link,
            closeKey,
            GitHubCommentOperationKind.Close,
            body: null,
            stateReason: "completed");

        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(5));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _fixture.Comments.CloseEntered = entered;
        _fixture.Comments.ReleaseClose = release;
        var worker = _fixture.Services.GetRequiredService<GitHubIssueCommentOperationRecoveryWorker>();
        var recovery = worker.ProcessPendingAsync();
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var newGithubIssueNumber = link.GithubIssueNumber + 1000;
        try
        {
            await ResetMirrorAsync(link.Id, link.GithubIssueNumber);
            await SetMirrorAsync(link.Id, newGithubIssueNumber);
        }
        finally
        {
            release.TrySetResult();
        }
        await recovery;

        var current = await LoadLinkAsync(projectId, issueNumber);
        Assert.Equal(newGithubIssueNumber, current!.GithubIssueNumber);
        Assert.False(current.HasPostedComment(closeKey));
        Assert.Null(await LoadOperationStatusAsync(link.Id, closeKey));
    }

    [Fact]
    public async Task RecoveryDefiniteFailureAfterMirrorResetDoesNotDeleteNewGenerationReservation()
    {
        var (projectId, issueNumber, _) = await CreateMirroredIssueAsync();
        var link = (await LoadLinkAsync(projectId, issueNumber))!;
        const string commentKey = "stale-definite-failure";
        await ReserveAndDeferOperationAsync(
            link,
            commentKey,
            GitHubCommentOperationKind.Comment,
            "old generation",
            stateReason: null);

        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(5));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _fixture.Comments.PostEntered = entered;
        _fixture.Comments.ReleasePost = release;
        var worker = _fixture.Services.GetRequiredService<GitHubIssueCommentOperationRecoveryWorker>();
        var recovery = worker.ProcessPendingAsync();
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        var oldGithubIssueNumber = link.GithubIssueNumber;
        var newGithubIssueNumber = oldGithubIssueNumber + 1000;
        try
        {
            await ResetMirrorAsync(link.Id, oldGithubIssueNumber);
            await SetMirrorAsync(link.Id, newGithubIssueNumber);
            await using var scope = _fixture.Services.CreateAsyncScope();
            var links = scope.ServiceProvider.GetRequiredService<GitHubIssueLinkStore>();
            Assert.True(await links.TryReserveCommentAsync(
                link.Id,
                commentKey,
                GitHubCommentOperationKind.Comment,
                "new generation",
                stateReason: null));
            _fixture.Comments.PostFailure = new InvalidOperationException("definite old-generation failure");
        }
        finally
        {
            release.TrySetResult();
        }
        await recovery;

        var current = await LoadLinkAsync(projectId, issueNumber);
        Assert.Equal(newGithubIssueNumber, current!.GithubIssueNumber);
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            var operation = await db.GitHubIssueCommentOperations.SingleOrDefaultAsync(item =>
                item.LinkId == link.Id && item.CommentKey == commentKey);
            Assert.NotNull(operation);
            Assert.Equal(newGithubIssueNumber, operation!.GithubIssueNumber);
            Assert.Equal(GitHubCommentOperationStatus.Reserved, operation.Status);
            Assert.Equal("new generation", operation.Body);
        }
    }

    [Fact]
    public async Task MultipleCommentMarkerMatches_FailClosedWithoutAnotherPost()
    {
        var (projectId, issueNumber, _) = await CreateMirroredIssueAsync();
        await ClearProjectionBookkeepingAsync(projectId, issueNumber);
        _fixture.Comments.Comments.Clear();
        _fixture.Comments.PostThenThrow = true;

        await DispatchContentChangeAsync(projectId, issueNumber);

        var link = await LoadLinkAsync(projectId, issueNumber);
        Assert.NotNull(link);
        var marker = GitHubCommentOperationMarker.For(link!.Id, GitHubCommentKinds.MirrorCreated);
        _fixture.Comments.Comments.Add(new RecordingGitHubCommentPort.PostedComment(
            "unused",
            link.GithubIssueNumber,
            GitHubMirrorMarker.Append("duplicate", marker)));
        // Use the same connection identity as the real comment so the marker
        // query sees both remote matches.
        var connection = (await _fixture.Services.GetRequiredService<GitHubConnectionStore>()
            .GetByRepositoryAsync(projectId, link.RepositoryName))!;
        _fixture.Comments.Comments[^1] = new RecordingGitHubCommentPort.PostedComment(
            connection.Id,
            link.GithubIssueNumber,
            GitHubMirrorMarker.Append("duplicate", marker));

        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(5));
        var worker = _fixture.Services.GetRequiredService<GitHubIssueCommentOperationRecoveryWorker>();
        Assert.True(await worker.ProcessPendingAsync() >= 1);

        link = await LoadLinkAsync(projectId, issueNumber);
        Assert.Equal(GitHubCommentOperationStatus.Ambiguous, await LoadOperationStatusAsync(link!.Id, GitHubCommentKinds.MirrorCreated));
        Assert.Equal(GitHubSyncStatus.Error, link.SyncStatus);
        Assert.Equal(2, _fixture.Comments.Comments.Count(
            comment => comment.GithubIssueNumber == link.GithubIssueNumber));
    }

    [Fact]
    public async Task UnknownCloseOutcome_IsReconciledFromRemoteStateWithoutDuplicateClose()
    {
        var (projectId, issueNumber, _) = await CreateMirroredIssueAsync();
        var grain = _fixture.Grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(projectId, issueNumber)));
        await grain.UpdateFullAsync(new UpdateIssueData(
            NoWorkflow: true,
            PresentFields: new HashSet<string>([nameof(UpdateIssueData.NoWorkflow)], StringComparer.Ordinal)));
        await grain.StartWorkAsync();
        await PumpAsync();
        await grain.MarkDoneAsync();
        await PumpAsync();

        var link = await LoadLinkAsync(projectId, issueNumber);
        Assert.NotNull(link);
        await ClearProjectionBookkeepingAsync(projectId, issueNumber);
        _fixture.Comments.Comments.Clear();
        _fixture.Comments.Closes.Clear();
        _fixture.Comments.Issues[link!.GithubIssueNumber] =
            _fixture.Comments.Issues[link.GithubIssueNumber] with { State = "open", StateReason = null };
        _fixture.Comments.CloseThenThrow = true;

        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/github/sync", new { });
        Assert.Equal(System.Net.HttpStatusCode.Conflict, response.StatusCode);
        Assert.Single(_fixture.Comments.Closes,
            close => close.GithubIssueNumber == link!.GithubIssueNumber);

        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(5));
        var worker = _fixture.Services.GetRequiredService<GitHubIssueCommentOperationRecoveryWorker>();
        Assert.True(await worker.ProcessPendingAsync() >= 1);

        Assert.Single(_fixture.Comments.Closes,
            close => close.GithubIssueNumber == link.GithubIssueNumber);
        link = await LoadLinkAsync(projectId, issueNumber);
        Assert.True(link!.HasPostedComment(GitHubCommentKinds.ClosedCompleted));
    }

    [Fact]
    public async Task EnableFailure_LeavesDurableReprojectionForHostedRecovery()
    {
        var (projectId, issueNumber, connectionId) = await CreateMirroredIssueAsync();
        using (var disabled = await _fixture.Client.PostAsync(
            $"/api/projects/{projectId}/github-connections/{connectionId}/disable", JsonContent.Create(new { })))
            disabled.EnsureSuccessStatusCode();

        _fixture.Comments.UpdatedIssues.Clear();
        _fixture.Comments.UpdateFailure = new InvalidOperationException("temporary update failure");
        using (var enabled = await _fixture.Client.PostAsync(
            $"/api/projects/{projectId}/github-connections/{connectionId}/enable", JsonContent.Create(new { })))
            enabled.EnsureSuccessStatusCode();

        var connection = await _fixture.Services.GetRequiredService<GitHubConnectionStore>()
            .GetByIdAsync(connectionId);
        Assert.True(connection!.NeedsReprojection);

        _fixture.Comments.UpdateFailure = null;
        var worker = _fixture.Services.GetRequiredService<GitHubConnectionReprojectionWorker>();
        Assert.True(await worker.ProcessPendingAsync() >= 1);

        connection = await _fixture.Services.GetRequiredService<GitHubConnectionStore>()
            .GetByIdAsync(connectionId);
        Assert.False(connection!.NeedsReprojection);
        var link = await LoadLinkAsync(projectId, issueNumber);
        Assert.Contains(_fixture.Comments.UpdatedIssues,
            update => update.GithubIssueNumber == link!.GithubIssueNumber);
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
    public async Task ManualLinkCannotDeleteReservedMirrorIntent()
    {
        var owner = $"octocat-{Guid.NewGuid():N}";
        var project = await _fixture.Client.CreateProjectWithDefaultRepositoryAsync<ProjectInfo>(
            "/api/projects", $"github-link-race-{Guid.NewGuid():N}", repoName: RepositoryName,
            gitUrl: $"https://github.com/{owner}/{RepositoryName}.git");
        var connection = await _fixture.Client.PostDataAsync<JsonElement>(
            $"/api/projects/{project.Id}/github-connections",
            new { owner, repo = RepositoryName });
        _fixture.Comments.CreateFailure = new InvalidOperationException("hold mirror creation");
        var issueNumber = await CreateIssueInProjectAsync(project.Id, isDraft: false);
        await PumpAsync();
        _fixture.Comments.CreateFailure = null;

        var link = await LoadLinkAsync(project.Id, issueNumber);
        Assert.NotNull(link);
        var githubIssueNumber = 818;
        _fixture.Comments.Issues[githubIssueNumber] = new GitHubIssueSnapshot(
            githubIssueNumber, "manual", "manual body", "open", null);
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var links = scope.ServiceProvider.GetRequiredService<GitHubIssueLinkStore>();
            await links.TryReserveMirrorCreateAsync(link!.Id);
        }

        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/issues/{issueNumber}/github/link",
            new { repository = $"{owner}/{RepositoryName}", number = githubIssueNumber });
        Assert.Equal(System.Net.HttpStatusCode.Conflict, response.StatusCode);

        var current = await LoadLinkAsync(project.Id, issueNumber);
        Assert.True(current!.IsPending);
        Assert.True(current.MirrorCreateAttempted);
        Assert.DoesNotContain(_fixture.Comments.CreatedIssues,
            created => created.ConnectionId == connection.GetProperty("id").GetString());
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
        var pausedRead = await _fixture.Client.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{projectId}/issues/{issueNumber}");
        Assert.Equal("paused", pausedRead.GetProperty("data").GetProperty("github").GetProperty("connectionStatus").GetString());
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

    private async Task ClearProjectionBookkeepingAsync(string projectId, int issueNumber)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var link = await db.GitHubIssueLinks.SingleAsync(row =>
            row.ProjectId == projectId && row.IssueNumber == issueNumber);
        link.PostedCommentsJson = "[]";
        link.StateLabel = null;
        await db.GitHubIssueCommentOperations
            .Where(operation => operation.LinkId == link.Id)
            .ExecuteDeleteAsync();
        await db.SaveChangesAsync();
    }

    private async Task ReserveAndDeferOperationAsync(
        GitHubIssueLink link,
        string commentKey,
        string kind,
        string? body,
        string? stateReason)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var links = scope.ServiceProvider.GetRequiredService<GitHubIssueLinkStore>();
        Assert.True(await links.TryReserveCommentAsync(
            link.Id,
            commentKey,
            kind,
            body,
            stateReason));
        Assert.NotNull(await links.DeferCommentOperationAsync(
            link.Id,
            commentKey,
            "simulated unknown outcome"));
    }

    private async Task ResetMirrorAsync(string linkId, int githubIssueNumber)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var links = scope.ServiceProvider.GetRequiredService<GitHubIssueLinkStore>();
        Assert.NotNull(await links.ResetMirrorAsync(linkId, githubIssueNumber));
    }

    private async Task SetMirrorAsync(string linkId, int githubIssueNumber)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var links = scope.ServiceProvider.GetRequiredService<GitHubIssueLinkStore>();
        Assert.NotNull(await links.SetMirrorAsync(linkId, githubIssueNumber));
    }

    private async Task<GitHubIssueLink?> LoadLinkAsync(string projectId, int issueNumber) =>
        await _fixture.Services.GetRequiredService<GitHubIssueLinkStore>().GetByIssueAsync(projectId, issueNumber);

    private async Task<string?> LoadOperationStatusAsync(string linkId, string commentKey)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        return await db.GitHubIssueCommentOperations
            .Where(operation => operation.LinkId == linkId && operation.CommentKey == commentKey)
            .Select(operation => operation.Status)
            .SingleOrDefaultAsync();
    }

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
