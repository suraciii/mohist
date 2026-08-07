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
using Orleans.TestingHost;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.GitHub;

/// <summary>
/// Recording fake for the minimal GitHub comment port. Deterministic seam:
/// production <see cref="GitHubCommentPort"/> (real HTTP) is never
/// resolvable in specs (design/testing.md hard constraint 1).
/// </summary>
public sealed class RecordingGitHubCommentPort : IGitHubCommentPort
{
    public sealed record PostedComment(string ConnectionId, int GithubIssueNumber, string Body);
    public sealed record StateLabelChange(string ConnectionId, int GithubIssueNumber, string StateLabel);
    public sealed record IssueClose(string ConnectionId, int GithubIssueNumber, string StateReason);

    public List<PostedComment> Comments { get; } = [];
    public List<StateLabelChange> StateLabels { get; } = [];
    public List<IssueClose> Closes { get; } = [];
    public string? DeliveryPrUrl { get; set; }

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
    private readonly TestClusterPortAllocator _portAllocator;
    private SqliteConnection _keeper = null!;

    public GitHubFeedFixture()
    {
        _connectionString = $"Data Source=github-feed-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _portAllocator = new TestClusterPortAllocator();
        var (siloPort, gatewayPort) = _portAllocator.AllocateConsecutivePortPairs(1);
        _factory = new GitHubFeedWebApplicationFactory(
            _connectionString,
            "/mohist-tests/github-feed/runner",
            "/mohist-tests/github-feed/system-update.json",
            "/mohist-tests/github-feed/logs",
            TimeProvider,
            siloPort,
            gatewayPort);
    }

    public IGrainFactory Grains => _factory.Services.GetRequiredService<IGrainFactory>();
    public HttpClient Client { get; private set; } = null!;
    public IServiceProvider Services => _factory.Services;
    public RecordingGitHubCommentPort Comments => _factory.Comments;
    public FakeTimeProvider TimeProvider { get; } = new(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero));

    public async ValueTask InitializeAsync()
    {
        _keeper = new SqliteConnection(_connectionString);
        await _keeper.OpenAsync();
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
        _portAllocator?.Dispose();
    }

    private sealed class GitHubFeedWebApplicationFactory : MohistWebApplicationFactory
    {
        public RecordingGitHubCommentPort Comments { get; } = new();

        public GitHubFeedWebApplicationFactory(
            string connectionString,
            string runnerRoot,
            string systemUpdateStatePath,
            string logsPath,
            FakeTimeProvider timeProvider,
            int siloPort,
            int gatewayPort)
            : base(connectionString, runnerRoot, systemUpdateStatePath, logsPath, timeProvider, siloPort, gatewayPort)
        {
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IGitHubCommentPort>();
                services.AddSingleton<IGitHubCommentPort>(Comments);
            });
        }
    }
}
