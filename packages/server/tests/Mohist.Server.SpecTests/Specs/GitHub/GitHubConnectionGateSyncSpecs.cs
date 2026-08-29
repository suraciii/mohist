using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.GitHub.Domain;
using Mohist.Server.GitHub.Infrastructure;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;
using Xunit;
namespace Mohist.Server.SpecTests.Specs.GitHub;

/// <summary>
/// Deterministic connection-gate race specifications: a Disable commit and
/// an outbound GitHub send arbitrate through the per-connection gate, and
/// injected fake time removes every real wait.
/// </summary>
public sealed partial class GitHubSyncSpecs
{
    [Fact]
    public async Task DisableCommittedBeforeGatedRecoverySend_RetainsOperationUntilEnabled()
    {
        var (projectId, issueNumber, connectionId) = await CreateMirroredIssueAsync();
        var link = (await LoadLinkAsync(projectId, issueNumber))!;
        const string commentKey = "gated-comment";
        await ReserveAndDeferOperationAsync(
            link,
            commentKey,
            GitHubCommentOperationKind.Comment,
            "gated comment",
            stateReason: null);
        // The deferred reservation becomes claimable only after its retry
        // delay; advance the injected clock instead of waiting.
        _fixture.TimeProvider.Advance(GitHubIssueLinkStore.RetryBaseDelay);

        // The barrier must belong to the operation under test: leftover
        // rows from earlier specs in this collection are claimable after the
        // clock advance and would otherwise satisfy (or block on) the race
        // synchronization before the gated row reaches it.
        var marker = GitHubCommentOperationMarker.For(link.Id, commentKey);
        var findEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFind = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _fixture.Comments.FindEntered = findEntered;
        _fixture.Comments.FindEnteredFilter = m => string.Equals(m, marker, StringComparison.Ordinal);
        _fixture.Comments.ReleaseFind = releaseFind;

        await using var scope = _fixture.Services.CreateAsyncScope();
        var connections = scope.ServiceProvider.GetRequiredService<GitHubConnectionStore>();
        var gate = scope.ServiceProvider.GetRequiredService<GitHubConnectionGate>();
        var holderStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHolder = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = gate.EnterAsync(connectionId, async _ =>
        {
            holderStarted.SetResult();
            await releaseHolder.Task;
            // Same async flow as the gate holder: the re-entrancy guard lets
            // this Disable commit while the connection gate is held.
            await connections.SetStatusAsync(projectId, connectionId, GitHubConnectionStatus.Disabled);
        });
        await holderStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        var worker = _fixture.Services.GetRequiredService<GitHubIssueCommentOperationRecoveryWorker>();
        var recovery = Task.Run(() => worker.ProcessPendingAsync(TestContext.Current.CancellationToken));
        await findEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        releaseFind.SetResult();
        releaseHolder.SetResult();
        await recovery.WaitAsync(TestContext.Current.CancellationToken);
        await holder;

        Assert.DoesNotContain(_fixture.Comments.Comments, comment =>
            comment.Body.Contains(marker, StringComparison.Ordinal));
        Assert.Equal(
            GitHubCommentOperationStatus.Reserved,
            await LoadOperationStatusAsync(link.Id, commentKey));
        var deferred = await LoadLinkAsync(projectId, issueNumber);
        Assert.NotNull(deferred);
        Assert.Equal(0, deferred!.PostedComments.Count(key =>
            string.Equals(key, commentKey, StringComparison.Ordinal)));
        var disabled = await connections.GetAsync(projectId, connectionId);
        Assert.Equal(GitHubConnectionStatus.Disabled, disabled!.Status);

        using (var enabled = await _fixture.Client.PostAsync(
            $"/api/projects/{projectId}/github-connections/{connectionId}/enable", JsonContent.Create(new { })))
            enabled.EnsureSuccessStatusCode();

        await worker.ProcessPendingAsync();
        var recovered = await LoadLinkAsync(projectId, issueNumber);
        Assert.NotNull(recovered);
        Assert.Equal(1, recovered!.PostedComments.Count(key =>
            string.Equals(key, commentKey, StringComparison.Ordinal)));
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
    public async Task DisableWaitsForInFlightCommentSendAndDoesNotDuplicateAfterEnable()
    {
        var (projectId, issueNumber, connectionId) = await CreateMirroredIssueAsync();
        var link = (await LoadLinkAsync(projectId, issueNumber))!;
        var grain = _fixture.Grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(projectId, issueNumber)));
        await grain.UpdateFullAsync(new UpdateIssueData(
            NoWorkflow: true,
            PresentFields: new HashSet<string>([nameof(UpdateIssueData.NoWorkflow)], StringComparer.Ordinal)));
        await grain.StartWorkAsync();
        await PumpAsync();

        var postEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _fixture.Comments.PostEntered = postEntered;
        _fixture.Comments.ReleasePost = releasePost;
        await grain.MarkDoneAsync();
        var pump = Task.Run(PumpAsync);
        await postEntered.Task.WaitAsync(TestContext.Current.CancellationToken);

        var disable = Task.Run(() => _fixture.Client.PostAsync(
            $"/api/projects/{projectId}/github-connections/{connectionId}/disable", JsonContent.Create(new { })));
        releasePost.SetResult();
        using (var disabled = await disable.WaitAsync(TestContext.Current.CancellationToken))
            disabled.EnsureSuccessStatusCode();
        await pump;

        // The comment send won the gate: it settled, including its posted
        // bookkeeping, before the Disable commit returned.
        var marker = GitHubCommentOperationMarker.For(link.Id, GitHubCommentKinds.Completed);
        Assert.Equal(1, _fixture.Comments.Comments.Count(comment =>
            comment.Body.Contains(marker, StringComparison.Ordinal)));
        Assert.Equal(
            GitHubCommentOperationStatus.Posted,
            await LoadOperationStatusAsync(link.Id, GitHubCommentKinds.Completed));

        using (var enabled = await _fixture.Client.PostAsync(
            $"/api/projects/{projectId}/github-connections/{connectionId}/enable", JsonContent.Create(new { })))
            enabled.EnsureSuccessStatusCode();

        var worker = _fixture.Services.GetRequiredService<GitHubIssueCommentOperationRecoveryWorker>();
        await worker.ProcessPendingAsync();
        Assert.Equal(1, _fixture.Comments.Comments.Count(comment =>
            comment.Body.Contains(marker, StringComparison.Ordinal)));
        Assert.Equal(1, _fixture.Comments.Closes.Count(close =>
            close.GithubIssueNumber == link.GithubIssueNumber
                && close.StateReason == "completed"));
        Assert.Equal(
            GitHubCommentOperationStatus.Posted,
            await LoadOperationStatusAsync(link.Id, GitHubCommentKinds.Completed));
        Assert.Equal(
            GitHubCommentOperationStatus.Posted,
            await LoadOperationStatusAsync(link.Id, GitHubCommentKinds.ClosedCompleted));
    }

}
