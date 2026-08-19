using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Slack.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

/// <summary>
/// Web host for the Slack control-plane progress routes with the three
/// outbound Slack ports replaced by the shared fakes. The production
/// adapters (registered by <c>AddSlackControlPlane</c>'s neighbours) make
/// real HTTP calls; here the port interfaces are overridden last so every
/// rotation / app-management / bot-identity result is deterministic and
/// offline. The shared operator token + loopback default come from
/// <see cref="MohistWebApplicationFactory"/>.
/// </summary>
public sealed class SlackControlPlaneRoutesFactory : MohistWebApplicationFactory
{
    public FakeSlackConfigurationCredentialPort Configuration { get; } = new();
    public FakeSlackAppManagementPort Apps { get; } = new();
    public FakeSlackBotIdentityVerificationPort BotIdentity { get; } = new();

    public SlackControlPlaneRoutesFactory(
        string connectionString,
        string runnerRoot,
        string systemUpdateStatePath,
        FakeTimeProvider timeProvider)
        : base(
            connectionString,
            runnerRoot,
            systemUpdateStatePath,
            "/mohist-tests/slack-control-plane/logs",
            timeProvider)
    {
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ISlackConfigurationCredentialPort>();
            services.AddSingleton<ISlackConfigurationCredentialPort>(Configuration);
            services.RemoveAll<ISlackAppManagementPort>();
            services.AddSingleton<ISlackAppManagementPort>(Apps);
            services.RemoveAll<ISlackAppManagementFactPort>();
            services.AddSingleton<ISlackAppManagementFactPort>(Apps);
            services.RemoveAll<ISlackBotIdentityVerificationPort>();
            services.AddSingleton<ISlackBotIdentityVerificationPort>(BotIdentity);
        });
    }
}

/// <summary>
/// Owns one Mohist test cluster + web host whose three Slack outbound
/// ports are the shared fakes. Shared by the whole SlackControlPlaneRoutes
/// collection; reset the fakes between tests via the exposed properties.
/// </summary>
public sealed class SlackControlPlaneRoutesFixture : IAsyncLifetime
{
    private SqliteConnection _keeper = null!;

    public SlackControlPlaneRoutesFactory Factory { get; private set; } = null!;
    public FakeTimeProvider TimeProvider { get; } = new(new DateTimeOffset(2026, 8, 5, 14, 0, 0, TimeSpan.Zero));
    public IServiceProvider Services => Factory.Services;
    public FakeSlackConfigurationCredentialPort Configuration => Factory.Configuration;
    public FakeSlackAppManagementPort Apps => Factory.Apps;
    public FakeSlackBotIdentityVerificationPort BotIdentity => Factory.BotIdentity;

    public async ValueTask InitializeAsync()
    {
        var connectionString = $"Data Source=slack-control-plane-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _keeper = new SqliteConnection(connectionString);
        await _keeper.OpenAsync();
        MigratedSqliteTemplate.CopyTo(_keeper);

        Factory = new SlackControlPlaneRoutesFactory(
            connectionString,
            "/mohist-tests/slack-control-plane/runner",
            "/mohist-tests/slack-control-plane/system-update.json",
            TimeProvider);
        await Factory.EnsureSchemaAsync();
        _ = Factory.Services;
    }

    public async ValueTask DisposeAsync()
    {
        Factory?.Dispose();
        if (_keeper is not null)
            await _keeper.DisposeAsync();
    }

    public HttpClient CreateOperatorClient()
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {MohistIntegrationFixture.OperatorToken}");
        return client;
    }

    public HttpClient CreateUnauthenticatedClient() => Factory.CreateClient();
}
