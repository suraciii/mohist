using System.Net;
using Mohist.Server.Infrastructure.Events;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mohist.Server.GitHub.Domain;
using Mohist.Server.GitHub.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.GitHub;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Project.Services;
using Mohist.Server.TestSupport;
using Xunit;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.Tests.GitHub;

[Collection("GitHubCommand")]
[Trait("level", "L1")]
public sealed class GitHubWriteBackSpecs
{
    private const string RepoName = "hello-world";
    private const int GithubIssueNumber = 42;

    private readonly GitHubCommandFixture _fixture;

    public GitHubWriteBackSpecs(GitHubCommandFixture fixture)
    {
        _fixture = fixture;
        fixture.Comments.Comments.Clear();
        fixture.Comments.StateLabels.Clear();
        fixture.Comments.Closes.Clear();
        fixture.Comments.DeliveryPrUrl = null;
        // The failure knobs are process-wide state on the shared fake; a
        // spec that arms one must not leak it into the next test in the
        // collection.
        fixture.Comments.PostFailure = null;
        fixture.Comments.PostThenThrow = false;
        fixture.Comments.LabelFailure = null;
        fixture.Comments.CloseFailures.Clear();
        fixture.Comments.CloseFailure = null;
        fixture.Comments.CloseThenThrow = false;
    }

    private HttpClient Client => _fixture.Client;

    private async Task<(string ProjectId, string ConnectionId)> ConnectNewAsync()
    {
        var owner = $"octocat-{Guid.NewGuid():N}";
        var project = await Client.CreateProjectWithDefaultRepositoryAsync<ProjectInfo>(
            "/api/projects", $"github-writeback-{Guid.NewGuid():N}", repoName: RepoName, gitUrl: $"https://github.com/{owner}/{RepoName}.git");
        var created = await Client.PostDataAsync<JsonElement>($"/api/projects/{project.Id}/github-connections", new
        {
            owner,
            repo = RepoName,
        });
        return (project.Id, created.GetProperty("id").GetString()!);
    }

    private async Task<int> SeedIssueAsync(string projectId, Action<DomainIssue> transition)
    {
        var grains = _fixture.Grains;
        var issueNumber = await grains.GetGrain<IIssueCounterGrain>(GrainKey.IssueCounter(projectId)).NextAsync();
        var issue = DomainIssue.Create(projectId, issueNumber, "Write back me", repositoryRef: RepoName, isDraft: false);
        transition(issue);
        // Link must exist before the issue events become dispatchable:
        // SaveAsync fires a fire-and-forget dispatch poke, and the
        // write-back handler drops the event for good when no link exists
        // yet (best-effort contract). The mirror link may not exist yet.
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var links = scope.ServiceProvider.GetRequiredService<GitHubIssueLinkStore>();
            await links.CreateAsync(projectId, RepoName, GithubIssueNumber, issueNumber);
        }
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IIssueStore>();
            await store.SaveAsync(GrainKey.Issue(new IssueKey(projectId, issueNumber)), issue, issue.PendingEvents);
        }
        return issueNumber;
    }

    /// <summary>
    /// Drives write-back to convergence. A failing operation now parks the
    /// stream under the dispatcher's attempt budget, so the loop keeps the
    /// explicit-drain boundary but advances the fake clock past the park
    /// backoff whenever the durable lease state shows a retry is not yet
    /// due. Bounded by the dispatcher's own attempt budget — never by a
    /// wall-clock sleep.
    /// </summary>
    private async Task PumpAsync()
    {
        var dispatcher = _fixture.Services.GetRequiredService<IEventDispatcher>();
        var dbFactory = _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        var options = _fixture.Services.GetRequiredService<IOptions<EventDispatcherOptions>>().Value;
        for (var round = 0; round <= options.MaxAttempts + 1; round++)
        {
            await dispatcher.DrainAsync();
            if (!await HasParkedRetryAsync(dbFactory, _fixture.TimeProvider.GetUtcNow()))
                return;
            // A parked stream keeps its lease until LeaseDuration, and its
            // retry is gated until NextAttemptAt; advancing past both makes
            // the next drain's fresh owner able to claim and re-drive it.
            _fixture.TimeProvider.Advance(
                options.LeaseDuration + options.MaxBackoff + options.BaseBackoff);
        }
    }

    /// <summary>
    /// True while any dispatch stream is parked in backoff. The timestamps
    /// are compared in memory so the pump is driven by durable lease state
    /// rather than a wall-clock wait.
    /// </summary>
    private static async Task<bool> HasParkedRetryAsync(
        IDbContextFactory<MohistDbContext> dbFactory,
        DateTimeOffset now)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var nextAttempts = await db.DispatchStreamLeases.AsNoTracking()
            .Where(l => l.NextAttemptAt != null)
            .Select(l => l.NextAttemptAt!.Value)
            .ToListAsync();
        return nextAttempts.Any(next => next > now);
    }

    [Fact]
    public async Task Completed_WithDeliveryPullRequest_PostsSummaryCommentWithPrUrlAndCloses()
    {
        var (projectId, connectionId) = await ConnectNewAsync();
        _fixture.Comments.DeliveryPrUrl = "https://github.com/octocat/hello-world/pull/123";
        var issueNumber = await SeedIssueAsync(projectId, issue =>
        {
            issue.StartWorkflow("wr_done");
            issue.Complete("wr_done");
        });

        await PumpAsync();

        var comment = Assert.Single(
            _fixture.Comments.Comments,
            c => c.ConnectionId == connectionId && c.Body.Contains("已完成"));
        Assert.Contains("https://github.com/octocat/hello-world/pull/123", comment.Body);
        Assert.Contains(
            GitHubStateLabels.Done,
            _fixture.Comments.StateLabels.Where(s => s.ConnectionId == connectionId).Select(s => s.StateLabel));
        var closes = _fixture.Comments.Closes.Where(c => c.ConnectionId == connectionId).ToList();
        if (closes.Count != 1)
            throw new Xunit.Sdk.XunitException(
                $"Expected exactly one close for connection {connectionId}, found {closes.Count}. " +
                $"{await DescribeWriteBackStateAsync(projectId, connectionId, issueNumber)}");
        var close = closes[0];
        Assert.Equal("completed", close.StateReason);
    }

    /// <summary>
    /// A close failure with a known remote outcome releases the reservation,
    /// so the dispatcher's redelivery re-reserves and re-executes only the
    /// close. Comment and label stay posted exactly once.
    /// </summary>
    [Fact]
    public async Task TransientCloseFailure_IsRedeliveredAndClosesOnce()
    {
        var startedAt = _fixture.TimeProvider.GetUtcNow();
        var (projectId, connectionId) = await ConnectNewAsync();
        _fixture.Comments.DeliveryPrUrl = "https://github.com/octocat/hello-world/pull/123";
        _fixture.Comments.CloseFailures.Enqueue(new HttpRequestException(
            "close endpoint blipped",
            null,
            HttpStatusCode.NotFound));
        var issueNumber = await SeedIssueAsync(projectId, issue =>
        {
            issue.StartWorkflow("wr_done");
            issue.Complete("wr_done");
        });

        await PumpAsync();

        var close = Assert.Single(_fixture.Comments.Closes, c => c.ConnectionId == connectionId);
        Assert.Equal("completed", close.StateReason);
        Assert.Single(_fixture.Comments.Comments,
            c => c.ConnectionId == connectionId && c.Body.Contains("已完成"));
        Assert.Single(_fixture.Comments.StateLabels,
            s => s.ConnectionId == connectionId && s.StateLabel == GitHubStateLabels.Done);
        Assert.Single(await FailuresAsync(projectId, connectionId, GitHubWriteBackOperation.Close));
        Assert.Empty(await DeadLettersAsync(startedAt));
    }

    /// <summary>
    /// A persistent failure burns the dispatcher's attempt budget and
    /// dead-letters the event; every attempt leaves its audit row, and the
    /// close is never sent.
    /// </summary>
    [Fact]
    public async Task PersistentCloseFailure_DeadLettersAfterTheAttemptBudget()
    {
        var startedAt = _fixture.TimeProvider.GetUtcNow();
        var (projectId, connectionId) = await ConnectNewAsync();
        _fixture.Comments.DeliveryPrUrl = "https://github.com/octocat/hello-world/pull/123";
        _fixture.Comments.CloseFailure = new HttpRequestException(
            "close endpoint is gone",
            null,
            HttpStatusCode.NotFound);
        var issueNumber = await SeedIssueAsync(projectId, issue =>
        {
            issue.StartWorkflow("wr_done");
            issue.Complete("wr_done");
        });

        await PumpAsync();

        Assert.DoesNotContain(_fixture.Comments.Closes, c => c.ConnectionId == connectionId);
        var failures = await FailuresAsync(projectId, connectionId, GitHubWriteBackOperation.Close);
        var deadLetter = Assert.Single(await DeadLettersAsync(startedAt));
        Assert.Equal(failures.Count, deadLetter.AttemptCount);
        Assert.Equal(EventCatalog.ReverseDns.IssueCompleted, deadLetter.Type);
        Assert.Equal(
            "Mohist.Server.GitHub.Subscriptions.GitHubWriteBackHandler",
            deadLetter.FailingHandler);
    }

    /// <summary>
    /// An unknown remote outcome keeps the reservation deferred, so the
    /// redelivery skips the held reservation instead of resending the close.
    /// </summary>
    [Fact]
    public async Task UnknownCloseOutcome_KeepsTheReservationAndNeverResends()
    {
        var startedAt = _fixture.TimeProvider.GetUtcNow();
        var (projectId, connectionId) = await ConnectNewAsync();
        _fixture.Comments.DeliveryPrUrl = "https://github.com/octocat/hello-world/pull/123";
        _fixture.Comments.CloseThenThrow = true;
        var issueNumber = await SeedIssueAsync(projectId, issue =>
        {
            issue.StartWorkflow("wr_done");
            issue.Complete("wr_done");
        });

        await PumpAsync();

        // The fake records the close before the unknown outcome, so exactly
        // one send happened; the deferred reservation blocks every redrive.
        Assert.Single(_fixture.Comments.Closes, c => c.ConnectionId == connectionId);
        var operation = await LoadCloseOperationAsync(projectId, issueNumber);
        Assert.NotNull(operation);
        Assert.NotNull(operation!.LastError);
        Assert.NotNull(operation.NextAttemptAt);
        Assert.Single(await FailuresAsync(projectId, connectionId, GitHubWriteBackOperation.Close));
        Assert.Empty(await DeadLettersAsync(startedAt));
    }

    private async Task<List<GitHubWriteBackFailure>> FailuresAsync(
        string projectId,
        string connectionId,
        string operation)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var failures = await scope.ServiceProvider
            .GetRequiredService<GitHubWriteBackFailureStore>()
            .ListRecentAsync(projectId, limit: 50);
        return failures
            .Where(f => f.ConnectionId == connectionId && f.Operation == operation)
            .ToList();
    }

    /// <summary>
    /// Dead letters written during this test on the fake clock. The store is
    /// shared by the whole collection, so the range keeps sibling tests'
    /// rows out of the assertion.
    /// </summary>
    private async Task<List<DeadLetterRow>> DeadLettersAsync(DateTimeOffset fromUtc)
    {
        var store = _fixture.Services.GetRequiredService<IDeadLetterStore>();
        var rows = await store.ListByTimeRangeAsync(
            fromUtc,
            _fixture.TimeProvider.GetUtcNow().AddTicks(1));
        return rows
            .Where(row => row.FailingHandler == "Mohist.Server.GitHub.Subscriptions.GitHubWriteBackHandler")
            .ToList();
    }

    private async Task<GitHubIssueCommentOperationRow?> LoadCloseOperationAsync(
        string projectId,
        int issueNumber)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        await using var db = await scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<MohistDbContext>>()
            .CreateDbContextAsync();
        var link = await db.GitHubIssueLinks.AsNoTracking()
            .SingleOrDefaultAsync(row => row.ProjectId == projectId && row.IssueNumber == issueNumber);
        return link is null
            ? null
            : await db.GitHubIssueCommentOperations.AsNoTracking()
                .SingleOrDefaultAsync(row =>
                    row.LinkId == link.Id && row.CommentKey == GitHubCommentKinds.ClosedCompleted);
    }

    /// <summary>
    /// Closes assertion names its cause: a failing operation parks the
    /// event on the dispatcher retry channel and leaves an audit row, and
    /// the reservation rows show whether an operation was skipped or held.
    /// </summary>
    private async Task<string> DescribeWriteBackStateAsync(string projectId, string connectionId, int issueNumber)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var failures = (await sp.GetRequiredService<GitHubWriteBackFailureStore>()
                .ListRecentAsync(projectId, limit: 20))
            .Where(f => f.ConnectionId == connectionId)
            .ToList();
        await using var db = await sp.GetRequiredService<IDbContextFactory<MohistDbContext>>()
            .CreateDbContextAsync();
        var link = await db.GitHubIssueLinks.AsNoTracking()
            .SingleOrDefaultAsync(row => row.ProjectId == projectId && row.IssueNumber == issueNumber);
        var ops = link is null
            ? "link=<none>"
            : string.Join("; ", db.GitHubIssueCommentOperations.AsNoTracking()
                .Where(row => row.LinkId == link.Id)
                .Select(row => $"{row.CommentKey}:{row.Kind}:err={row.LastError ?? "-"}"));
        return $"failures=[{string.Join("|", failures.Select(f => $"{f.Operation}:{f.ErrorCode}:{f.ErrorDetail}"))}] ops=[{ops}]";
    }
}
