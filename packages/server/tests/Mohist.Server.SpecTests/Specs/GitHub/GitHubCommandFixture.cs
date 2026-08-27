using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.GitHub.Domain;
using Mohist.Server.GitHub.Infrastructure;
using Mohist.Server.GitHub.Ports;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.GitHub;

/// <summary>
/// Recording fake for the minimal GitHub comment port. Deterministic seam:
/// production <see cref="GitHubCommentPort"/> (real HTTP) is never
/// resolvable in specs (design/testing.md "No External Environment").
/// </summary>
public sealed class RecordingGitHubCommentPort : IGitHubCommentPort, IGitHubIssuePort
{
    public sealed record PostedComment(string ConnectionId, int GithubIssueNumber, string Body);
    public sealed record CreatedIssue(string ConnectionId, int GithubIssueNumber, string Title, string Body, string Marker);
    public sealed record UpdatedIssue(string ConnectionId, int GithubIssueNumber, string Title, string Body, string Marker);
    public sealed record StateLabelChange(string ConnectionId, int GithubIssueNumber, string StateLabel);
    public sealed record IssueClose(string ConnectionId, int GithubIssueNumber, string StateReason);

    public List<PostedComment> Comments { get; } = [];
    public List<CreatedIssue> CreatedIssues { get; } = [];
    public List<UpdatedIssue> UpdatedIssues { get; } = [];
    public List<StateLabelChange> StateLabels { get; } = [];
    public Queue<int?> MarkerMatches { get; } = new();
    public int MarkerMatchCount { get; set; }
    public Exception? CreateFailure { get; set; }
    public Exception? FindFailure { get; set; }
    public Exception? ConfirmationFailure { get; set; }
    public bool PostThenThrow { get; set; }
    public TaskCompletionSource? PostEntered { get; set; }
    public TaskCompletionSource? ReleasePost { get; set; }
    public Exception? UpdateFailure { get; set; }
    public Queue<Exception> UpdateFailures { get; } = new();
    public Exception? LabelFailure { get; set; }
    public Exception? CloseFailure { get; set; }
    public bool CloseThenThrow { get; set; }
    public TaskCompletionSource? CloseEntered { get; set; }
    public TaskCompletionSource? ReleaseClose { get; set; }
    public bool CreateThenThrow { get; set; }
    public int NextGithubIssueNumber { get; set; } = 900;
    public int? CreateIssueNumberOverride { get; set; }
    public List<IssueClose> Closes { get; } = [];
    public Dictionary<int, GitHubIssueSnapshot> Issues { get; } = new();
    public string? DeliveryPrUrl { get; set; }

    public Task<int> CreateIssueAsync(
        GitHubConnection connection,
        string title,
        string body,
        string marker,
        CancellationToken ct = default)
    {
        if (CreateFailure is not null) throw CreateFailure;
        var number = CreateIssueNumberOverride ?? NextGithubIssueNumber++;
        var mirroredBody = GitHubMirrorMarker.Append(body, marker);
        CreatedIssues.Add(new CreatedIssue(connection.Id, number, title, mirroredBody, marker));
        Issues[number] = new GitHubIssueSnapshot(number, title, mirroredBody, "open", null);
        if (CreateThenThrow)
            throw new TimeoutException("simulated unknown create outcome");
        return Task.FromResult(number);
    }

    public Task<int?> FindIssueByMarkerAsync(
        GitHubConnection connection,
        string marker,
        CancellationToken ct = default)
    {
        if (FindFailure is not null) throw FindFailure;
        if (MarkerMatchCount > 1)
        {
            MarkerMatchCount = 0;
            throw new InvalidOperationException("GitHub mirror marker matched multiple issues");
        }
        if (MarkerMatches.Count > 0)
            return Task.FromResult(MarkerMatches.Dequeue());
        return Task.FromResult<int?>(null);
    }

    public Task<GitHubIssueSnapshot?> GetIssueAsync(
        GitHubConnection connection,
        int githubIssueNumber,
        CancellationToken ct = default)
    {
        return Task.FromResult<GitHubIssueSnapshot?>(Issues.TryGetValue(githubIssueNumber, out var issue)
            ? issue
            : new GitHubIssueSnapshot(githubIssueNumber, "Existing GitHub issue", "Existing body", "open", null));
    }

    public Task UpdateIssueAsync(
        GitHubConnection connection,
        int githubIssueNumber,
        string title,
        string body,
        string marker,
        CancellationToken ct = default)
    {
        if (UpdateFailures.Count > 0) throw UpdateFailures.Dequeue();
        if (UpdateFailure is not null) throw UpdateFailure;
        var mirroredBody = GitHubMirrorMarker.Append(body, marker);
        UpdatedIssues.Add(new UpdatedIssue(connection.Id, githubIssueNumber, title, mirroredBody, marker));
        var prior = Issues.TryGetValue(githubIssueNumber, out var existing)
            ? existing
            : null;
        Issues[githubIssueNumber] = new GitHubIssueSnapshot(
            githubIssueNumber,
            title,
            mirroredBody,
            prior?.State ?? "open",
            prior?.StateReason);
        return Task.CompletedTask;
    }

    public async Task PostCommentAsync(
        GitHubConnection connection,
        int githubIssueNumber,
        string body,
        CancellationToken ct = default)
    {
        if (ConfirmationFailure is not null) throw ConfirmationFailure;
        Comments.Add(new PostedComment(connection.Id, githubIssueNumber, body));
        PostEntered?.TrySetResult();
        if (ReleasePost is not null)
            await ReleasePost.Task.WaitAsync(ct);
        if (PostThenThrow)
        {
            PostThenThrow = false;
            throw new TimeoutException("simulated unknown reply outcome");
        }
    }

    public Task<IReadOnlyList<string>> FindCommentIdsByMarkerAsync(
        GitHubConnection connection,
        int githubIssueNumber,
        string marker,
        CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>(Comments
            .Select((comment, index) => (comment, index))
            .Where(item => item.comment.ConnectionId == connection.Id
                && item.comment.GithubIssueNumber == githubIssueNumber
                && item.comment.Body.Contains(marker, StringComparison.Ordinal))
            .Select(item => (item.index + 1).ToString())
            .ToArray());

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

    public async Task CloseIssueAsync(
        GitHubConnection connection,
        int githubIssueNumber,
        string stateReason,
        CancellationToken ct = default)
    {
        if (CloseFailure is not null) throw CloseFailure;
        Closes.Add(new IssueClose(connection.Id, githubIssueNumber, stateReason));
        CloseEntered?.TrySetResult();
        if (ReleaseClose is not null)
            await ReleaseClose.Task.WaitAsync(ct);
        var prior = Issues.TryGetValue(githubIssueNumber, out var existing)
            ? existing
            : new GitHubIssueSnapshot(githubIssueNumber, "Existing GitHub issue", "Existing body", "open", null);
        Issues[githubIssueNumber] = prior with { State = "closed", StateReason = stateReason };
        if (CloseThenThrow)
        {
            CloseThenThrow = false;
            throw new TimeoutException("simulated unknown close outcome");
        }
    }

    public Task<string?> FindDeliveryPullRequestUrlAsync(
        GitHubConnection connection,
        int issueNumber,
        CancellationToken ct = default) =>
        Task.FromResult(DeliveryPrUrl);
}

/// <summary>
/// Integration fixture for the GitHub command/close translators. Hosts the
/// same production stack as <see cref="MohistIntegrationFixture"/> but
/// swaps <see cref="IGitHubCommentPort"/> for the recording fake, so no
/// spec can reach the real GitHub API. Own collection (<c>GitHubCommand</c>)
/// keeps the fake out of the shared <c>IntegrationRunner</c> collection.
/// </summary>
public sealed class GitHubCommandFixture : IAsyncLifetime
{
    private readonly GitHubCommandWebApplicationFactory _factory;
    private readonly string _connectionString;
    private SqliteConnection _keeper = null!;

    public GitHubCommandFixture()
    {
        _connectionString = $"Data Source=github-command-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _factory = new GitHubCommandWebApplicationFactory(
            _connectionString,
            "/mohist-tests/github-command/runner",
            "/mohist-tests/github-command/system-update.json",
            "/mohist-tests/github-command/logs",
            TimeProvider);
    }

    public IGrainFactory Grains => _factory.Services.GetRequiredService<IGrainFactory>();
    public HttpClient Client { get; private set; } = null!;
    public IServiceProvider Services => _factory.Services;
    public RecordingGitHubCommentPort Comments => _factory.Comments;
    public FakeTimeProvider TimeProvider { get; } = new(TestTime.UtcNow);

    public async ValueTask InitializeAsync()
    {
        _keeper = new SqliteConnection(_connectionString);
        await _keeper.OpenAsync();
        MigratedSqliteTemplate.CopyTo(_keeper);
        Client = _factory.CreateClient();
        Client.DefaultRequestHeaders.Add("Authorization", $"Bearer {MohistIntegrationFixture.OperatorToken}");
        await _factory.EnsureSchemaAsync();
    }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        _factory?.Dispose();
        if (_keeper is not null)
            await _keeper.DisposeAsync();
    }

    private sealed class GitHubCommandWebApplicationFactory : MohistWebApplicationFactory
    {
        public RecordingGitHubCommentPort Comments { get; } = new();

        public GitHubCommandWebApplicationFactory(
            string connectionString,
            string runnerRoot,
            string systemUpdateStatePath,
            string logsPath,
            FakeTimeProvider timeProvider)
            : base(connectionString, runnerRoot, systemUpdateStatePath, logsPath, timeProvider)
        {
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureTestServices(services =>
            {
                // These specs advance a fake clock and invoke the delivery
                // pass explicitly. Do not let the autonomous hosted loop
                // consume a due row before the assertion does.
                services.Configure<GitHubCommandReplyDeliveryOptions>(options =>
                    options.HostedWorkerEnabled = false);
                // The same fake-clock rule applies to event ingress: PumpAsync
                // is the explicit dispatch boundary for these lifecycle specs.
                services.Configure<EventDispatcherOptions>(options =>
                    options.WorkerCount = 0);
                services.RemoveAll<IGitHubCommentPort>();
                services.RemoveAll<IGitHubIssuePort>();
                services.AddSingleton<IGitHubCommentPort>(Comments);
                services.AddSingleton<IGitHubIssuePort>(Comments);
            });
        }
    }
}
