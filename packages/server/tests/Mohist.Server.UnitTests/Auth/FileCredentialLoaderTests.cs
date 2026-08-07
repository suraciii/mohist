using EnvironmentAbstractions.TestHelpers;
using Microsoft.Extensions.Configuration;
using Mohist.Server.Auth.Domain;
using Mohist.Server.Auth.Identity;
using Mohist.Server.Infrastructure.Hosting;
using Xunit;

namespace Mohist.Server.UnitTests.Auth;

public sealed class FileCredentialLoaderTests
{
    private const string AdminToken = "test-admin-token-0123456789abcdef";
    private const string ServiceToken = "test-service-token-0123456789abcdef";
    private const string Home = "/mohist-tests/credentials";

    [Fact]
    public void ConfiguredTokens_ResolveToAdminAndServicePrincipals()
    {
        var loader = NewLoader(ConfigurationWith(
            ("Mohist:AdminToken", AdminToken),
            ("Mohist:OperatorToken", ServiceToken)));

        var admin = loader.TryResolve(AdminToken);
        Assert.NotNull(admin);
        Assert.Equal(MohistPrincipal.AdminPrincipalId, admin.Id);
        Assert.Equal(PrincipalKind.Admin, admin.Kind);
        Assert.Contains(Scope.Operator, admin.Scopes);

        var service = loader.TryResolve(ServiceToken);
        Assert.NotNull(service);
        Assert.Equal(MohistPrincipal.ServicePrincipalId, service.Id);
        Assert.Equal(PrincipalKind.Service, service.Kind);

        Assert.Null(loader.TryResolve("unrelated-token-0123456789abcdef"));
    }

    [Fact]
    public void MissingFiles_AreGeneratedOnceAndReused()
    {
        var store = new InMemoryCredentialStore();
        var environment = EnvironmentWith(home: Home);
        var configuration = new ConfigurationBuilder().Build();

        var first = new FileCredentialLoader(configuration, environment, store);
        var second = new FileCredentialLoader(configuration, environment, store);

        Assert.Equal(2, store.CreateCount);
        var adminPath = Path.Combine(Home, ".mohist", "admin-token");
        var servicePath = Path.Combine(Home, ".mohist", "operator-token");
        var adminToken = store.ReadExplicit(adminPath);
        var serviceToken = store.ReadExplicit(servicePath);
        Assert.True(adminToken.Length >= 32);
        Assert.True(serviceToken.Length >= 32);
        Assert.NotEqual(adminToken, serviceToken);

        Assert.Equal(
            first.TryResolve(adminToken)?.Id,
            second.TryResolve(adminToken)?.Id);
        Assert.Equal(
            first.TryResolve(serviceToken)?.Id,
            second.TryResolve(serviceToken)?.Id);
    }

    [Fact]
    public void ExistingFiles_AreNotOverwritten()
    {
        var store = new InMemoryCredentialStore();
        store.Set(Path.Combine(Home, ".mohist", "admin-token"), AdminToken);
        store.Set(Path.Combine(Home, ".mohist", "operator-token"), ServiceToken);

        var loader = NewLoader(new ConfigurationBuilder().Build(), store: store);

        Assert.Equal(0, store.CreateCount);
        Assert.NotNull(loader.TryResolve(AdminToken));
        Assert.NotNull(loader.TryResolve(ServiceToken));
    }

    [Fact]
    public void EnvironmentToken_OverridesConfigToken()
    {
        var environment = EnvironmentWith(home: Home);
        environment[FileCredentialLoader.AdminTokenEnvironmentVariable] = "env-admin-token-0123456789abcdef";
        var loader = NewLoader(
            ConfigurationWith(("Mohist:AdminToken", AdminToken)),
            environment);

        Assert.Null(loader.TryResolve(AdminToken));
        Assert.NotNull(loader.TryResolve("env-admin-token-0123456789abcdef"));
    }

    [Fact]
    public void ExplicitPath_ReadsManagedToken()
    {
        const string path = "/mohist-tests/credentials/explicit/admin-token";
        var store = new InMemoryCredentialStore();
        store.Set(path, AdminToken);
        var configuration = ConfigurationWith(("Mohist:AdminTokenPath", path));

        var loader = NewLoader(configuration, store: store);

        Assert.NotNull(loader.TryResolve(AdminToken));
        Assert.Equal(path, store.LastExplicitPath);
    }

    [Fact]
    public void EnvironmentPath_OverridesConfigPath()
    {
        const string configPath = "/mohist-tests/credentials/config/admin-token";
        const string envPath = "/mohist-tests/credentials/env/admin-token";
        var store = new InMemoryCredentialStore();
        store.Set(envPath, AdminToken);
        var environment = EnvironmentWith(home: Home);
        environment[FileCredentialLoader.AdminTokenPathEnvironmentVariable] = envPath;
        var configuration = ConfigurationWith(("Mohist:AdminTokenPath", configPath));

        var loader = NewLoader(configuration, environment, store);

        Assert.NotNull(loader.TryResolve(AdminToken));
        Assert.Equal(envPath, store.LastExplicitPath);
    }

    [Fact]
    public void ExplicitPath_ReportsReadFailure()
    {
        const string path = "/mohist-tests/credentials/missing/admin-token";
        var configuration = ConfigurationWith(("Mohist:AdminTokenPath", path));

        var error = Assert.Throws<InvalidOperationException>(
            () => NewLoader(configuration, store: new InMemoryCredentialStore()));

        Assert.Contains(path, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ShortTokens_AreRejected()
    {
        var configuration = ConfigurationWith(
            ("Mohist:AdminToken", "short"),
            ("Mohist:OperatorToken", ServiceToken));

        var error = Assert.Throws<InvalidOperationException>(
            () => NewLoader(configuration));
        Assert.Contains("admin", error.Message, StringComparison.Ordinal);
    }

    private static FileCredentialLoader NewLoader(
        IConfiguration configuration,
        MockEnvironmentVariableProvider? environment = null,
        InMemoryCredentialStore? store = null) =>
        new(configuration, environment ?? EnvironmentWith(home: Home), store ?? new InMemoryCredentialStore());

    private static IConfiguration ConfigurationWith(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(
                pair => pair.Key,
                pair => (string?)pair.Value))
            .Build();

    private static MockEnvironmentVariableProvider EnvironmentWith(string home)
    {
        var environment = new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false);
        environment[MohistServiceRegistration.HomeEnvironmentVariable] = home;
        return environment;
    }

    private sealed class InMemoryCredentialStore : IFileCredentialStore
    {
        private readonly Dictionary<string, string> _tokens = new(StringComparer.Ordinal);

        public int CreateCount { get; private set; }

        public string? LastExplicitPath { get; private set; }

        public void Set(string path, string token) => _tokens[path] = token;

        public string LoadOrCreateDefault(string path)
        {
            if (_tokens.TryGetValue(path, out var token))
                return token;

            CreateCount++;
            token = $"generated-{CreateCount}-token-0123456789abcdef";
            _tokens[path] = token;
            return token;
        }

        public string ReadExplicit(string path)
        {
            LastExplicitPath = path;
            if (!_tokens.TryGetValue(path, out var token))
                throw new InvalidOperationException(
                    $"Mohist credential could not be read from '{path}': missing.");
            return token;
        }
    }
}
