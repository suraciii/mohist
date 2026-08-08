using EnvironmentAbstractions.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Auth.Domain;
using Mohist.Server.Auth.Identity;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Project.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Auth;

/// <summary>
/// Shared plumbing for <see cref="AuthResolutionMiddleware"/> tests:
/// file-credential + in-memory credential store wiring, DefaultHttpContext
/// construction with optional endpoint metadata, and the 401/403 assertion
/// helpers.
/// </summary>
internal static class AuthResolutionTestSupport
{
    internal const string AdminToken = "test-admin-token-0123456789abcdef";
    internal const string ServiceToken = "test-operator-token-0123456789abcdef";

    internal static void AssertUnauthorized(HttpContext context)
    {
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Equal(
            "Bearer error=\"invalid_token\"",
            Assert.Single(context.Response.Headers.WWWAuthenticate));
    }

    internal static void AssertForbidden(HttpContext context)
    {
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Equal(0, context.Response.Headers.WWWAuthenticate.Count);
    }

    internal static string ReadBody(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, System.Text.Encoding.UTF8);
        return reader.ReadToEnd();
    }

    internal static (AuthResolutionMiddleware Middleware, DefaultHttpContext Context) NewReadonlyContext(
        string path,
        string? method = null,
        RouteScopeRequirement? endpoint = null)
    {
        var token = CredentialToken.Generate(CredentialKind.Pat);
        var db = new FakeCredentialStore();
        db.Add(new Credential(
            "cred_readonly",
            "agent-readonly",
            CredentialKind.Pat,
            CredentialToken.Hash(token),
            [Scope.Readonly],
            "spec",
            "moh_pat_ab",
            null,
            new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero),
            null,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        var context = NewContext(request => request.Headers.Authorization = $"Bearer {token}");
        context.Request.Path = path;
        context.Request.Method = method ?? HttpMethods.Get;
        if (endpoint is not null)
            SetEndpoint(context, endpoint);
        return (NewMiddleware(db), context);
    }

    internal static (AuthResolutionMiddleware Middleware, DefaultHttpContext Context) NewRunnerContext(
        string path,
        string? method = null,
        RouteScopeRequirement? endpoint = null,
        bool runnerEndpoint = true)
    {
        var token = CredentialToken.Generate(CredentialKind.Runner);
        var db = new FakeCredentialStore();
        db.Add(new Credential(
            "cred_runner_a",
            MohistPrincipal.AdminPrincipalId,
            CredentialKind.Runner,
            CredentialToken.Hash(token),
            [Scope.Runner],
            "runner-a",
            "moh_runner_ab",
            null,
            null,
            null,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        var context = NewContext(request => request.Headers.Authorization = $"Bearer {token}");
        context.Request.Path = path;
        context.Request.Method = method ?? HttpMethods.Get;
        SetEndpoint(context, endpoint ?? (runnerEndpoint
            ? new RouteScopeRequirement(RouteScopeRequirementExtensions.Runner)
            : null));
        return (NewMiddleware(db), context);
    }

    internal static void SetEndpoint(HttpContext context, params object?[] metadata) =>
        context.SetEndpoint(new Endpoint(
            requestDelegate: null,
            new EndpointMetadataCollection(
                metadata.Where(item => item is not null).Cast<object>().ToArray()),
            displayName: "test"));

    internal static AuthResolutionMiddleware NewMiddleware(
        FakeCredentialStore? db = null,
        ProjectRefResolver? projects = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mohist:AdminToken"] = AdminToken,
                ["Mohist:OperatorToken"] = ServiceToken,
            })
            .Build();
        var environment = new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false);
        var loader = new FileCredentialLoader(configuration, environment, new FakeFileStore());
        return new AuthResolutionMiddleware(
            loader,
            db ?? new FakeCredentialStore(),
            projects ?? Projects.Resolver);
    }

    internal static ProjectResolverSetup Projects { get; } =
        CreateProjectResolver(("proj_a", "alpha"), ("proj_b", "beta"));

    internal static ProjectResolverSetup CreateProjectResolver(params (string Id, string Name)[] projects)
    {
        var connection = new SqliteConnection($"Data Source=auth-middleware-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE "Projects" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_Projects" PRIMARY KEY,
                    "Name" TEXT NOT NULL,
                    "RepositoriesJson" TEXT NOT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "UpdatedAt" TEXT NOT NULL,
                    "RepositoryRevision" INTEGER NOT NULL,
                    "LastRepositoryCommandJson" TEXT NULL
                );
                CREATE TABLE "ProjectWorkflowProfiles" (
                    "ProjectId" TEXT NOT NULL CONSTRAINT "PK_ProjectWorkflowProfiles" PRIMARY KEY,
                    "Variables" TEXT NOT NULL
                );
                """;
            command.ExecuteNonQuery();
        }

        foreach (var (id, name) in projects)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO "Projects" ("Id", "Name", "RepositoriesJson", "CreatedAt", "UpdatedAt", "RepositoryRevision", "LastRepositoryCommandJson")
                VALUES ($id, $name, '[]', '2026-01-01T00:00:00+00:00', '2026-01-01T00:00:00+00:00', 0, NULL);
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$name", name);
            command.ExecuteNonQuery();
        }

        var services = new ServiceCollection();
        services.AddDbContextFactory<MohistDbContext>(options => options.UseSqlite(connection));
        var provider = services.BuildServiceProvider();
        return new ProjectResolverSetup(
            new ProjectRefResolver(new ProjectQuerier(
                provider.GetRequiredService<IDbContextFactory<MohistDbContext>>())),
            provider,
            connection);
    }

    internal sealed record ProjectResolverSetup(
        ProjectRefResolver Resolver,
        ServiceProvider Provider,
        SqliteConnection Connection) : IDisposable
    {
        public void Dispose()
        {
            Provider.Dispose();
            Connection.Dispose();
        }
    }

    internal static DefaultHttpContext NewContext(
        Action<HttpRequest>? configure = null,
        string path = "/api/projects")
    {
        var context = new DefaultHttpContext
        {
            Request =
            {
                Method = HttpMethods.Get,
                Path = path,
            },
        };
        context.Response.Body = new MemoryStream();
        configure?.Invoke(context.Request);
        return context;
    }

    internal sealed class FakeFileStore : IFileCredentialStore
    {
        public int CreateCount { get; private set; }

        public string LoadOrCreateDefault(string path) =>
            throw new InvalidOperationException("Tokens are always configured in these tests.");

        public string ReadExplicit(string path) =>
            throw new InvalidOperationException("Tokens are always configured in these tests.");
    }

    internal sealed class FakeCredentialStore : ICredentialStore
    {
        // Models the documented ICredentialStore contract: rows that are
        // missing, revoked, or expired are read as not-found.
        private static readonly DateTimeOffset Now = new(2026, 6, 30, 0, 0, 0, TimeSpan.Zero);
        private readonly Dictionary<string, Credential> _byHash = new(StringComparer.Ordinal);

        public void Add(Credential credential) => _byHash[credential.TokenHash] = credential;

        public Task<Credential?> FindActiveAsync(string tokenHash, CancellationToken ct = default)
        {
            if (!_byHash.TryGetValue(tokenHash, out var credential)
                || credential.RevokedAt is not null
                || credential.ExpiresAt is { } expiresAt && expiresAt <= Now)
            {
                return Task.FromResult<Credential?>(null);
            }

            return Task.FromResult<Credential?>(credential);
        }

        public Task<PatCreateResult> CreatePatAsync(
            string principalId,
            string name,
            IReadOnlyList<Scope> scopes,
            DateTimeOffset expiresAt,
            CancellationToken ct = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<Credential>> ListPatAsync(
            string principalId,
            CancellationToken ct = default)
        {
            throw new NotSupportedException();
        }

        public Task<bool> RevokePatAsync(
            string principalId,
            string name,
            DateTimeOffset revokedAt,
            CancellationToken ct = default)
        {
            throw new NotSupportedException();
        }

        public Task CreateAsync(Credential credential, CancellationToken ct = default)
        {
            _byHash[credential.TokenHash] = credential;
            return Task.CompletedTask;
        }

        public Task<bool> RevokeAsync(string tokenHash, DateTimeOffset revokedAt, CancellationToken ct = default)
        {
            if (_byHash.TryGetValue(tokenHash, out var credential) && credential.RevokedAt is null)
            {
                _byHash[tokenHash] = credential with { RevokedAt = revokedAt };
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }

        public Task<EnrollmentTokenCreateResult> CreateEnrollmentTokenAsync(
            DateTimeOffset expiresAt,
            CancellationToken ct = default)
        {
            throw new NotSupportedException();
        }

        public Task<EnrollmentTokenConsumeStatus> ConsumeEnrollmentTokenAsync(
            string tokenHash,
            DateTimeOffset now,
            CancellationToken ct = default)
        {
            throw new NotSupportedException();
        }

        public Task<RunnerCredentialCreateResult?> CreateRunnerCredentialAsync(
            string principalId,
            string runnerId,
            CancellationToken ct = default)
        {
            throw new NotSupportedException();
        }

        public Task<bool> RevokeRunnerCredentialAsync(
            string runnerId,
            DateTimeOffset revokedAt,
            CancellationToken ct = default)
        {
            throw new NotSupportedException();
        }

        public Task<IntegrationCreateResult> CreateIntegrationAsync(
            string principalId,
            string name,
            string projectId,
            CancellationToken ct = default)
        {
            throw new NotSupportedException();
        }

        public Task<bool> RevokeIntegrationAsync(
            string principalId,
            string id,
            DateTimeOffset revokedAt,
            CancellationToken ct = default)
        {
            throw new NotSupportedException();
        }
    }
}
