using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure.Security;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Slack.Domain;
using Mohist.Server.Slack.Services;
using Mohist.Server.SpecTests.Support;
using Orleans.TestingHost;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

/// <summary>
/// Operator authentication for the lease route specs: the shared operator
/// token header plus an explicit <c>X-Mohist-Operator-Id</c> header must
/// both be present, mirroring the production
/// <see cref="SlackAdapterOperatorAuthenticator"/> contract without touching
/// <see cref="OperatorCredential"/>.
/// </summary>
public sealed class FakeSlackAdapterOperatorAuthenticator(string operatorToken)
    : ISlackAdapterOperatorAuthenticator
{
    public Task<string?> AuthenticateAsync(IHeaderDictionary headers, CancellationToken ct = default)
    {
        if (!string.Equals(
                headers[OperatorCredential.HeaderName].ToString(),
                operatorToken,
                StringComparison.Ordinal)
            || !headers.TryGetValue(SlackAdapterOperatorAuthenticator.OperatorIdHeaderName, out var values)
            || values.Count != 1)
        {
            return Task.FromResult<string?>(null);
        }

        var operatorId = values[0]?.Trim();
        return Task.FromResult(string.IsNullOrWhiteSpace(operatorId) ? null : operatorId);
    }
}

public sealed class FakeSlackLeaseSecretResolver : ISlackLeaseSecretResolver
{
    private readonly Dictionary<SecretStoreAddress, string> _tokens = new();

    public void Put(SlackLeaseTargetRef @ref, SecretKind kind, string token) =>
        _tokens[AddressFor(@ref, kind)] = token;

    public Task<string?> LoadAsync(SecretStoreAddress address, CancellationToken ct = default) =>
        Task.FromResult(_tokens.TryGetValue(address, out var token) ? token : null);

    private static SecretStoreAddress AddressFor(SlackLeaseTargetRef @ref, SecretKind kind) =>
        @ref switch
        {
            SlackLeaseTargetRef.Manager manager =>
                SecretStoreAddress.ForSlackWorkspaceEnrollment(manager.EnrollmentId, kind),
            SlackLeaseTargetRef.Connection connection =>
                SecretStoreAddress.ForAgentConnection(connection.ProjectId, connection.ConnectionId, kind),
            _ => throw new InvalidOperationException("Unsupported lease target ref."),
        };
}

/// <summary>
/// Web host for the lease routes with the full Mohist test composition but
/// the lease core fully faked: in-memory store, seeded in-memory target
/// registry, fake secret resolver and fake operator authenticator. The
/// fixture host is shared by the whole SlackLeaseRoutes collection.
/// </summary>
public sealed class SlackAdapterLeaseRoutesFactory : MohistWebApplicationFactory
{
    public InMemorySlackLeaseTargetProvider Targets { get; } = new();
    public FakeSlackLeaseSecretResolver Secrets { get; } = new();

    public SlackAdapterLeaseRoutesFactory(
        string connectionString,
        string runnerRoot,
        string systemUpdateStatePath,
        FakeTimeProvider timeProvider,
        int siloPort,
        int gatewayPort)
        : base(
            connectionString,
            runnerRoot,
            systemUpdateStatePath,
            "/mohist-tests/slack-leases/logs",
            timeProvider,
            siloPort,
            gatewayPort)
    {
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ISlackLeaseStore>();
            services.AddSingleton<ISlackLeaseStore, InMemorySlackLeaseStore>();
            services.RemoveAll<ISlackLeaseTargetProvider>();
            services.AddSingleton<ISlackLeaseTargetProvider>(Targets);
            services.RemoveAll<ISlackLeaseSecretResolver>();
            services.AddSingleton<ISlackLeaseSecretResolver>(Secrets);
            services.RemoveAll<ISlackAdapterOperatorAuthenticator>();
            services.AddSingleton<ISlackAdapterOperatorAuthenticator>(
                new FakeSlackAdapterOperatorAuthenticator(MohistIntegrationFixture.OperatorToken));
        });
    }
}

public sealed class SlackAdapterLeaseRoutesFixture : IAsyncLifetime
{
    private SqliteConnection _keeper = null!;
    private TestClusterPortAllocator? _portAllocator;

    public SlackAdapterLeaseRoutesFactory Factory { get; private set; } = null!;
    public FakeTimeProvider TimeProvider { get; } = new(new DateTimeOffset(2026, 8, 5, 14, 0, 0, TimeSpan.Zero));
    public InMemorySlackLeaseTargetProvider Targets => Factory.Targets;
    public FakeSlackLeaseSecretResolver Secrets => Factory.Secrets;

    public async ValueTask InitializeAsync()
    {
        var connectionString = $"Data Source=slack-leases-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _keeper = new SqliteConnection(connectionString);
        await _keeper.OpenAsync();

        _portAllocator = new TestClusterPortAllocator();
        var (siloPort, gatewayPort) = _portAllocator.AllocateConsecutivePortPairs(1);

        Factory = new SlackAdapterLeaseRoutesFactory(
            connectionString,
            "/mohist-tests/slack-leases/runner",
            "/mohist-tests/slack-leases/system-update.json",
            TimeProvider,
            siloPort,
            gatewayPort);
        await Factory.EnsureSchemaAsync();
        _ = Factory.Services;
    }

    public async ValueTask DisposeAsync()
    {
        Factory?.Dispose();
        _portAllocator?.Dispose();
        if (_keeper is not null)
            await _keeper.DisposeAsync();
    }

    public HttpClient CreateUnauthenticatedClient() => Factory.CreateClient();

    public HttpClient CreateOperatorClient(string operatorId)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Add(OperatorCredential.HeaderName, MohistIntegrationFixture.OperatorToken);
        client.DefaultRequestHeaders.Add(SlackAdapterOperatorAuthenticator.OperatorIdHeaderName, operatorId);
        return client;
    }

    public HttpClient CreateTokenOnlyClient()
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Add(OperatorCredential.HeaderName, MohistIntegrationFixture.OperatorToken);
        return client;
    }
}
