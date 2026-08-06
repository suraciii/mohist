using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.GitHub.Domain;
using Mohist.Server.GitHub.Infrastructure;
using Mohist.Server.GitHub.Ports;
using Mohist.Server.GitHub.Subscriptions;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.GitHub;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Issue.Domain.Events;
using Xunit;

namespace Mohist.Server.UnitTests.GitHub;

public sealed class GitHubWriteBackHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 17, 8, 0, 0, TimeSpan.Zero);

    private sealed class FakeSecretStore : ISecretStore
    {
        public Task StoreAsync(SecretStoreAddress address, byte[] plaintext, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task<byte[]?> LoadAsync(SecretStoreAddress address, CancellationToken ct = default) =>
            Task.FromResult<byte[]?>(null);
        public Task<bool> DeleteAsync(SecretStoreAddress address, CancellationToken ct = default) =>
            Task.FromResult(true);
        public IReadOnlyDictionary<string, string> Redact(IReadOnlyDictionary<string, string> values) => values;
    }

    private sealed class FakeCommentPort : IGitHubCommentPort
    {
        public sealed record PostedComment(string ConnectionId, int GithubIssueNumber, string Body);
        public sealed record StateLabelChange(string ConnectionId, int GithubIssueNumber, string StateLabel);
        public sealed record IssueClose(string ConnectionId, int GithubIssueNumber, string StateReason);

        public List<PostedComment> Comments { get; } = [];
        public List<StateLabelChange> StateLabels { get; } = [];
        public List<IssueClose> Closes { get; } = [];

        public Exception? CommentFailure { get; set; }
        public Exception? LabelFailure { get; set; }
        public Exception? CloseFailure { get; set; }
        public Exception? DeliveryLookupFailure { get; set; }
        public string? DeliveryPrUrl { get; set; }

        public Task PostCommentAsync(
            GitHubConnection connection,
            int githubIssueNumber,
            string body,
            CancellationToken ct = default)
        {
            if (CommentFailure is not null) throw CommentFailure;
            Comments.Add(new PostedComment(connection.Id, githubIssueNumber, body));
            return Task.CompletedTask;
        }

        public Task ReplaceStateLabelAsync(
            GitHubConnection connection,
            int githubIssueNumber,
            string stateLabel,
            CancellationToken ct = default)
        {
            if (LabelFailure is not null) throw LabelFailure;
            StateLabels.Add(new StateLabelChange(connection.Id, githubIssueNumber, stateLabel));
            return Task.CompletedTask;
        }

        public Task CloseIssueAsync(
            GitHubConnection connection,
            int githubIssueNumber,
            string stateReason,
            CancellationToken ct = default)
        {
            if (CloseFailure is not null) throw CloseFailure;
            Closes.Add(new IssueClose(connection.Id, githubIssueNumber, stateReason));
            return Task.CompletedTask;
        }

        public Task<string?> FindDeliveryPullRequestUrlAsync(
            GitHubConnection connection,
            int issueNumber,
            CancellationToken ct = default)
        {
            if (DeliveryLookupFailure is not null) throw DeliveryLookupFailure;
            return Task.FromResult(DeliveryPrUrl);
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<MohistDbContext> options)
        : IDbContextFactory<MohistDbContext>
    {
        public MohistDbContext CreateDbContext() => new(options);
        public Task<MohistDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class Harness
    {
        public SqliteConnection Keeper { get; }
        public DbContextOptions<MohistDbContext> Options { get; }
        public FakeCommentPort Port { get; } = new();
        public ServiceProvider Services { get; }

        public Harness()
        {
            Keeper = new SqliteConnection("Data Source=:memory:");
            Keeper.Open();
            Options = new DbContextOptionsBuilder<MohistDbContext>()
                .UseSqlite(Keeper)
                .Options;
            using (var db = new MohistDbContext(Options))
            {
                db.Database.EnsureCreated();
                db.GitHubConnections.Add(new GitHubConnectionRow
                {
                    Id = "conn-1",
                    ProjectId = "project-1",
                    Owner = "octo",
                    Repo = "hello",
                    RepositoryName = "hello-world",
                    IntakeLabel = "mohist",
                    FeedMode = GitHubFeedMode.Start,
                    ApproversJson = "[]",
                    Status = GitHubConnectionStatus.Active,
                    IdentityKind = GitHubIdentityKind.Pat,
                    NeedsAttention = false,
                    CreatedAt = Now,
                    UpdatedAt = Now,
                });
                db.GitHubIssueLinks.Add(new GitHubIssueLinkRow
                {
                    Id = "link-1",
                    ProjectId = "project-1",
                    RepositoryName = "hello-world",
                    GithubIssueNumber = 42,
                    IssueNumber = 7,
                    PostedCommentsJson = "[]",
                    CreatedAt = Now,
                    UpdatedAt = Now,
                });
                db.SaveChanges();
            }

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IDbContextFactory<MohistDbContext>>(new TestDbContextFactory(Options));
            services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now));
            services.AddSingleton<ISecretStore>(new FakeSecretStore());
            services.AddSingleton<IGitHubCommentPort>(Port);
            services.AddScoped<GitHubIssueLinkStore>();
            services.AddScoped<GitHubConnectionStore>();
            services.AddScoped<GitHubWriteBackFailureStore>();
            services.AddScoped<GitHubWriteBackHandler>();
            Services = services.BuildServiceProvider();
        }

        public async Task<GitHubWriteBackHandler> NewHandlerAsync()
        {
            var scope = Services.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<GitHubWriteBackHandler>();
            await scope.DisposeAsync();
            return handler;
        }

        public async Task<GitHubIssueLink> LinkAsync()
        {
            await using var db = new MohistDbContext(Options);
            var row = await db.GitHubIssueLinks.AsNoTracking().SingleAsync(r => r.Id == "link-1");
            var posted = System.Text.Json.JsonSerializer.Deserialize<List<string>>(row.PostedCommentsJson) ?? [];
            return new GitHubIssueLink
            {
                Id = row.Id,
                ProjectId = row.ProjectId,
                RepositoryName = row.RepositoryName,
                GithubIssueNumber = row.GithubIssueNumber,
                IssueNumber = row.IssueNumber,
                PostedComments = new HashSet<string>(posted, StringComparer.Ordinal),
                StateLabel = row.StateLabel,
                CreatedAt = row.CreatedAt,
                UpdatedAt = row.UpdatedAt,
            };
        }

        public async Task<GitHubConnection?> ConnectionAsync()
        {
            await using var db = new MohistDbContext(Options);
            var row = await db.GitHubConnections.AsNoTracking().SingleAsync(r => r.Id == "conn-1");
            return new GitHubConnection
            {
                Id = row.Id,
                ProjectId = row.ProjectId,
                Owner = row.Owner,
                Repo = row.Repo,
                RepositoryName = row.RepositoryName,
                IntakeLabel = row.IntakeLabel,
                FeedMode = row.FeedMode,
                Approvers = [],
                Status = row.Status,
                IdentityKind = row.IdentityKind,
                InstallationId = row.InstallationId,
                NeedsAttention = row.NeedsAttention,
                CreatedAt = row.CreatedAt,
                UpdatedAt = row.UpdatedAt,
            };
        }

        public async Task<IReadOnlyList<GitHubWriteBackFailure>> FailuresAsync()
        {
            await using var db = new MohistDbContext(Options);
            return await db.GitHubWriteBackFailures.AsNoTracking()
                .Select(r => new GitHubWriteBackFailure
                {
                    ProjectId = r.ProjectId,
                    ConnectionId = r.ConnectionId,
                    GithubIssueNumber = r.GithubIssueNumber,
                    IssueNumber = r.IssueNumber,
                    EventType = r.EventType,
                    Operation = r.Operation,
                    ErrorCode = r.ErrorCode,
                    ErrorDetail = r.ErrorDetail,
                })
                .ToListAsync();
        }
    }

    private static CloudEvent Event(string type, JsonElement? data = null) => new(
        id: $"evt-{Guid.NewGuid():N}",
        source: new Uri("mohist://test"),
        type: type,
        time: Now,
        data: data,
        extensions: new Dictionary<string, string>
        {
            [EventCatalog.Lineage.ProjectId] = "project-1",
            [EventCatalog.Lineage.Issue] = "7",
        });

    [Fact]
    public async Task WorkStarted_ProjectsInProgressLabelAndComment()
    {
        var harness = new Harness();
        var handler = await harness.NewHandlerAsync();

        await handler.HandleAsync(Event(EventCatalog.ReverseDns.IssueWorkStarted), CancellationToken.None);

        var label = Assert.Single(harness.Port.StateLabels);
        Assert.Equal(GitHubStateLabels.InProgress, label.StateLabel);
        var comment = Assert.Single(harness.Port.Comments);
        Assert.Contains("Mohist issue #7", comment.Body);
        var link = await harness.LinkAsync();
        Assert.Equal(GitHubStateLabels.InProgress, link.StateLabel);
        Assert.Contains(GitHubCommentKinds.WorkStarted, link.PostedComments);
    }

    [Fact]
    public async Task WorkStarted_RedeliveryIsIdempotent()
    {
        var harness = new Harness();
        var handler = await harness.NewHandlerAsync();
        var evt = Event(EventCatalog.ReverseDns.IssueWorkStarted);

        await handler.HandleAsync(evt, CancellationToken.None);
        await handler.HandleAsync(evt, CancellationToken.None);

        Assert.Single(harness.Port.StateLabels);
        Assert.Single(harness.Port.Comments);
    }

    [Fact]
    public async Task ApprovalRequested_ProjectsAwaitingApprovalLabelAndComment()
    {
        var harness = new Harness();
        var handler = await harness.NewHandlerAsync();

        await handler.HandleAsync(Event(EventCatalog.ReverseDns.StageApprovalRequested), CancellationToken.None);

        var label = Assert.Single(harness.Port.StateLabels);
        Assert.Equal(GitHubStateLabels.AwaitingApproval, label.StateLabel);
        Assert.Single(harness.Port.Comments);
    }

    [Fact]
    public async Task RunFailed_ProjectsBlockedLabelWithoutComment()
    {
        var harness = new Harness();
        var handler = await harness.NewHandlerAsync();

        await handler.HandleAsync(Event(EventCatalog.ReverseDns.WorkflowRunFailed), CancellationToken.None);

        var label = Assert.Single(harness.Port.StateLabels);
        Assert.Equal(GitHubStateLabels.Blocked, label.StateLabel);
        Assert.Empty(harness.Port.Comments);
    }

    [Fact]
    public async Task Completed_CommentsLabelsDoneAndClosesWithCompletedReason()
    {
        var harness = new Harness();
        var handler = await harness.NewHandlerAsync();

        await handler.HandleAsync(Event(EventCatalog.ReverseDns.IssueCompleted), CancellationToken.None);

        Assert.Single(harness.Port.Comments);
        Assert.Contains(GitHubStateLabels.Done, harness.Port.StateLabels.Select(s => s.StateLabel));
        var close = Assert.Single(harness.Port.Closes);
        Assert.Equal("completed", close.StateReason);
        var link = await harness.LinkAsync();
        Assert.Contains(GitHubCommentKinds.Completed, link.PostedComments);
        Assert.Contains(GitHubCommentKinds.ClosedCompleted, link.PostedComments);
    }

    [Fact]
    public async Task Completed_WithDeliveryPullRequest_IncludesPrUrlInCommentAndIsIdempotent()
    {
        var harness = new Harness();
        harness.Port.DeliveryPrUrl = "https://github.com/octo/hello/pull/123";
        var handler = await harness.NewHandlerAsync();
        var evt = Event(EventCatalog.ReverseDns.IssueCompleted);

        await handler.HandleAsync(evt, CancellationToken.None);
        await handler.HandleAsync(evt, CancellationToken.None);

        var comment = Assert.Single(harness.Port.Comments);
        Assert.Equal(GitHubWriteBackComments.Completed(7, "https://github.com/octo/hello/pull/123"), comment.Body);
        Assert.Contains("https://github.com/octo/hello/pull/123", comment.Body);
        var link = await harness.LinkAsync();
        Assert.Contains(GitHubCommentKinds.Completed, link.PostedComments);
    }

    [Fact]
    public async Task Completed_WithoutDeliveryPullRequest_PostsLegalCommentWithoutPrUrl()
    {
        var harness = new Harness();
        var handler = await harness.NewHandlerAsync();

        await handler.HandleAsync(Event(EventCatalog.ReverseDns.IssueCompleted), CancellationToken.None);

        var comment = Assert.Single(harness.Port.Comments);
        Assert.Equal(GitHubWriteBackComments.Completed(7), comment.Body);
        Assert.DoesNotContain("交付 PR", comment.Body);
        Assert.Single(harness.Port.Closes);
    }

    [Fact]
    public async Task Completed_DeliveryLookupFails_RecordsFailureAndStillPostsComment()
    {
        var harness = new Harness();
        harness.Port.DeliveryLookupFailure = new InvalidOperationException("lookup boom");
        var handler = await harness.NewHandlerAsync();

        await handler.HandleAsync(Event(EventCatalog.ReverseDns.IssueCompleted), CancellationToken.None);

        var comment = Assert.Single(harness.Port.Comments);
        Assert.Equal(GitHubWriteBackComments.Completed(7), comment.Body);
        Assert.Single(harness.Port.Closes);
        var failure = Assert.Single(await harness.FailuresAsync());
        Assert.Equal(GitHubWriteBackOperation.DeliveryPullRequest, failure.Operation);
        Assert.Equal(nameof(InvalidOperationException), failure.ErrorCode);
    }

    [Fact]
    public async Task Cancelled_WithReason_IncludesReasonInCommentAndIsIdempotent()
    {
        var harness = new Harness();
        var handler = await harness.NewHandlerAsync();
        var evt = Event(EventCatalog.ReverseDns.IssueCancelled,
            JsonSerializer.SerializeToElement(new IssueCancelled("需求方撤回"), CloudEvent.JsonOptions));

        await handler.HandleAsync(evt, CancellationToken.None);
        await handler.HandleAsync(evt, CancellationToken.None);

        var comment = Assert.Single(harness.Port.Comments);
        Assert.Equal(GitHubWriteBackComments.Cancelled(7, "需求方撤回"), comment.Body);
        Assert.Contains("需求方撤回", comment.Body);
        Assert.Single(harness.Port.Closes);
    }

    [Fact]
    public async Task Cancelled_WithoutReason_PostsGenericComment()
    {
        var harness = new Harness();
        var handler = await harness.NewHandlerAsync();

        await handler.HandleAsync(Event(EventCatalog.ReverseDns.IssueCancelled), CancellationToken.None);

        var comment = Assert.Single(harness.Port.Comments);
        Assert.Equal(GitHubWriteBackComments.Cancelled(7), comment.Body);
        Assert.DoesNotContain("原因", comment.Body);
    }

    [Fact]
    public async Task Cancelled_CommentsAndClosesWithNotPlannedReason()
    {
        var harness = new Harness();
        var handler = await harness.NewHandlerAsync();

        await handler.HandleAsync(Event(EventCatalog.ReverseDns.IssueCancelled), CancellationToken.None);

        Assert.Single(harness.Port.Comments);
        Assert.Empty(harness.Port.StateLabels);
        var close = Assert.Single(harness.Port.Closes);
        Assert.Equal("not_planned", close.StateReason);
    }

    [Fact]
    public async Task UnlinkedIssue_SkipsWriteBack()
    {
        var harness = new Harness();
        var handler = await harness.NewHandlerAsync();
        var evt = new CloudEvent(
            id: "evt-unlinked",
            source: new Uri("mohist://test"),
            type: EventCatalog.ReverseDns.IssueCompleted,
            time: Now,
            data: null,
            extensions: new Dictionary<string, string>
            {
                [EventCatalog.Lineage.ProjectId] = "project-1",
                [EventCatalog.Lineage.Issue] = "999",
            });

        await handler.HandleAsync(evt, CancellationToken.None);

        Assert.Empty(harness.Port.Comments);
        Assert.Empty(harness.Port.StateLabels);
        Assert.Empty(harness.Port.Closes);
    }

    [Fact]
    public async Task UnsupportedType_IsFilteredOut()
    {
        var harness = new Harness();
        var handler = await harness.NewHandlerAsync();

        Assert.False(handler.Filter(Event(EventCatalog.ReverseDns.IssueCreated)));
        Assert.True(handler.Filter(Event(EventCatalog.ReverseDns.IssueCompleted)));
    }

    [Fact]
    public async Task FailedOperation_IsRecordedAndDoesNotBlockRemainingOperations()
    {
        var harness = new Harness();
        harness.Port.CommentFailure = new InvalidOperationException("comment boom");
        var handler = await harness.NewHandlerAsync();

        await handler.HandleAsync(Event(EventCatalog.ReverseDns.IssueCompleted), CancellationToken.None);

        Assert.Empty(harness.Port.Comments);
        Assert.Single(harness.Port.StateLabels);
        var close = Assert.Single(harness.Port.Closes);
        Assert.Equal("completed", close.StateReason);
        var failure = Assert.Single(await harness.FailuresAsync());
        Assert.Equal(GitHubWriteBackOperation.Comment, failure.Operation);
        Assert.Equal(nameof(InvalidOperationException), failure.ErrorCode);
        var connection = await harness.ConnectionAsync();
        Assert.False(connection!.NeedsAttention);
    }

    [Fact]
    public async Task CredentialFailure_RecordsFailureAndMarksConnectionNeedsAttention()
    {
        var harness = new Harness();
        harness.Port.LabelFailure = new HttpRequestException("Forbidden", inner: null, HttpStatusCode.Forbidden);
        var handler = await harness.NewHandlerAsync();

        await handler.HandleAsync(Event(EventCatalog.ReverseDns.WorkflowRunFailed), CancellationToken.None);

        Assert.Empty(harness.Port.StateLabels);
        var failure = Assert.Single(await harness.FailuresAsync());
        Assert.Equal(GitHubWriteBackOperation.Label, failure.Operation);
        Assert.Equal("403", failure.ErrorCode);
        var connection = await harness.ConnectionAsync();
        Assert.True(connection!.NeedsAttention);
    }
}
