using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.GitHub.Domain;
using Mohist.Server.GitHub.Ports;
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
    public bool CreateThenThrow { get; set; }
    public int NextGithubIssueNumber { get; set; } = 900;
    public List<IssueClose> Closes { get; } = [];
    public string? DeliveryPrUrl { get; set; }

    public Task<int> CreateIssueAsync(
        GitHubConnection connection,
        string title,
        string body,
        string marker,
        CancellationToken ct = default)
    {
        if (CreateFailure is not null) throw CreateFailure;
        var number = NextGithubIssueNumber++;
        CreatedIssues.Add(new CreatedIssue(connection.Id, number, title, GitHubMirrorMarker.Append(body, marker), marker));
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

    public Task UpdateIssueAsync(
        GitHubConnection connection,
        int githubIssueNumber,
        string title,
        string body,
        string marker,
        CancellationToken ct = default)
    {
        UpdatedIssues.Add(new UpdatedIssue(connection.Id, githubIssueNumber, title, GitHubMirrorMarker.Append(body, marker), marker));
        return Task.CompletedTask;
    }

    public Task PostCommentAsync(
        GitHubConnection connection,
        int githubIssueNumber,
        string body,
        CancellationToken ct = default)
    {
        Comments.Add(new PostedComment(connection.Id, githubIssueNumber, body));
        return Task.CompletedTask;
    }

    public Task ReplaceStateLabelAsync(
        GitHubConnection connection,
        int githubIssueNumber,
        string stateLabel,
        CancellationToken ct = default)
    {
        StateLabels.Add(new StateLabelChange(connection.Id, githubIssueNumber, stateLabel));
        return Task.CompletedTask;
    }

    public Task CloseIssueAsync(
        GitHubConnection connection,
        int githubIssueNumber,
        string stateReason,
        CancellationToken ct = default)
    {
        Closes.Add(new IssueClose(connection.Id, githubIssueNumber, stateReason));
        return Task.CompletedTask;
    }

    public Task<string?> FindDeliveryPullRequestUrlAsync(
        GitHubConnection connection,
        int issueNumber,
        CancellationToken ct = default) =>
        Task.FromResult(DeliveryPrUrl);
}

/// <summary>
/// Integration fixture for the GitHub feed/close translators. Hosts the
/// same production stack as <see cref="MohistIntegrationFixture"/> but
/// swaps <see cref="IGitHubCommentPort"/> for the recording fake, so no
/// spec can reach the real GitHub API. Own collection (<c>GitHubFeed</c>)
/// keeps the fake out of the shared <c>IntegrationRunner</c> collection.
/// </summary>
public sealed class GitHubFeedFixture : IAsyncLifetime
{
    private readonly GitHubFeedWebApplicationFactory _factory;
    private readonly string _connectionString;
    private SqliteConnection _keeper = null!;

    public GitHubFeedFixture()
    {
        _connectionString = $"Data Source=github-feed-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _factory = new GitHubFeedWebApplicationFactory(
            _connectionString,
            "/mohist-tests/github-feed/runner",
            "/mohist-tests/github-feed/system-update.json",
            "/mohist-tests/github-feed/logs",
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

    private sealed class GitHubFeedWebApplicationFactory : MohistWebApplicationFactory
    {
        public RecordingGitHubCommentPort Comments { get; } = new();

        public GitHubFeedWebApplicationFactory(
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
                services.RemoveAll<IGitHubCommentPort>();
                services.RemoveAll<IGitHubIssuePort>();
                services.AddSingleton<IGitHubCommentPort>(Comments);
                services.AddSingleton<IGitHubIssuePort>(Comments);
            });
        }
    }
}
