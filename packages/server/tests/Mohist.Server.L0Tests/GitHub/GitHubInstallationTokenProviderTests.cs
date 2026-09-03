using Microsoft.Extensions.Time.Testing;
using Mohist.Server.GitHub.Ports;
using Xunit;

namespace Mohist.Server.L0Tests.GitHub;

[Trait("level", "L0")]
public sealed class GitHubInstallationTokenProviderTests
{
    [Fact]
    public async Task ConcurrentColdRequestsExchangeOnce()
    {
        var client = new FakeClient();
        var provider = new GitHubInstallationTokenProvider(client, new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => provider.GetAsync("installation-1")));
        Assert.All(results, result => Assert.Equal("token-1", result.AccessToken));
        Assert.Equal(1, client.ExchangeCount);
    }

    [Fact]
    public async Task ExpiredTokenIsReplaced()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var client = new FakeClient
        {
            TokenLifetime = TimeSpan.FromMinutes(2),
        };
        var provider = new GitHubInstallationTokenProvider(client, time);
        var first = await provider.GetAsync("installation-1");
        time.Advance(TimeSpan.FromMinutes(1).Add(TimeSpan.FromSeconds(1)));
        var second = await provider.GetAsync("installation-1");
        Assert.NotEqual(first.AccessToken, second.AccessToken);
        Assert.Equal(2, client.ExchangeCount);
    }

    [Fact]
    public async Task ConcurrentRefreshAfterPreviousGateWasReleasedExchangesOnce()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var client = new FakeClient { TokenLifetime = TimeSpan.FromMinutes(2) };
        var provider = new GitHubInstallationTokenProvider(client, time);
        await provider.GetAsync("installation-1");
        provider.Invalidate("installation-1", "token-1");

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => provider.GetAsync("installation-1")));

        Assert.All(results, result => Assert.Equal("token-2", result.AccessToken));
        Assert.Equal(2, client.ExchangeCount);
    }

    [Fact]
    public async Task InvalidateDoesNotRemoveReplacementToken()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var provider = new GitHubInstallationTokenProvider(new FakeClient(), time);
        var first = await provider.GetAsync("installation-1");
        provider.Invalidate("installation-1", "other-token");
        var cached = await provider.GetAsync("installation-1");
        Assert.Equal(first.AccessToken, cached.AccessToken);
        provider.Invalidate("installation-1", first.AccessToken);
        var refreshed = await provider.GetAsync("installation-1");
        Assert.NotEqual(first.AccessToken, refreshed.AccessToken);
    }

    private sealed class FakeClient : IGitHubAppClient
    {
        private int _exchangeCount;
        public int ExchangeCount => _exchangeCount;
        public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromHours(1);

        public Task<GitHubRepositoryInstallation> DiscoverInstallationAsync(string owner, string repo, CancellationToken ct = default) =>
            Task.FromResult(new GitHubRepositoryInstallation("installation-1", owner, repo, "repository-node-1"));

        public Task<GitHubInstallationToken> CreateInstallationTokenAsync(string installationId, CancellationToken ct = default)
        {
            var number = Interlocked.Increment(ref _exchangeCount);
            return Task.FromResult(new GitHubInstallationToken($"token-{number}", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).Add(TokenLifetime)));
        }
    }
}
