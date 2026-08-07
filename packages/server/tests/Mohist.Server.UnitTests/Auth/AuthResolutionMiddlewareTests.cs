using System.Net;
using System.Text;
using EnvironmentAbstractions.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Mohist.Server.Auth.Domain;
using Mohist.Server.Auth.Identity;
using Mohist.Server.Infrastructure.Data.Auth;
using Xunit;

namespace Mohist.Server.UnitTests.Auth;

public sealed class AuthResolutionMiddlewareTests
{
    private const string AdminToken = "test-admin-token-0123456789abcdef";
    private const string ServiceToken = "test-operator-token-0123456789abcdef";

    [Fact]
    public async Task BearerAdminToken_ResolvesTheAdminPrincipal()
    {
        var middleware = NewMiddleware();
        var context = NewContext(request => request.Headers.Authorization = $"Bearer {AdminToken}");

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        var principal = Assert.IsType<MohistPrincipal>(context.Items[MohistPrincipal.HttpContextItemKey]);
        Assert.Equal(MohistPrincipal.AdminPrincipalId, principal.Id);
        Assert.Equal(PrincipalKind.Admin, principal.Kind);
        Assert.Equal(HttpStatusCode.OK, (HttpStatusCode)context.Response.StatusCode);
    }

    [Fact]
    public async Task BearerServiceToken_ResolvesTheServicePrincipal()
    {
        var middleware = NewMiddleware();
        var context = NewContext(request => request.Headers.Authorization = $"Bearer {ServiceToken}");

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        var principal = Assert.IsType<MohistPrincipal>(context.Items[MohistPrincipal.HttpContextItemKey]);
        Assert.Equal(MohistPrincipal.ServicePrincipalId, principal.Id);
        Assert.Equal(PrincipalKind.Service, principal.Kind);
    }

    [Fact]
    public async Task SessionCookie_ResolvesThePrincipal()
    {
        var middleware = NewMiddleware();
        var context = NewContext(request =>
            request.Headers.Cookie = $"{AuthResolutionMiddleware.SessionCookieName}={AdminToken}");

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        var principal = Assert.IsType<MohistPrincipal>(context.Items[MohistPrincipal.HttpContextItemKey]);
        Assert.Equal(MohistPrincipal.AdminPrincipalId, principal.Id);
    }

    [Fact]
    public async Task BearerHeader_TakesPrecedenceOverACookie()
    {
        var middleware = NewMiddleware();
        var context = NewContext(request =>
        {
            request.Headers.Authorization = $"Bearer {ServiceToken}";
            request.Headers.Cookie = $"{AuthResolutionMiddleware.SessionCookieName}=not-a-real-token";
        });

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        var principal = Assert.IsType<MohistPrincipal>(context.Items[MohistPrincipal.HttpContextItemKey]);
        Assert.Equal(MohistPrincipal.ServicePrincipalId, principal.Id);
    }

    [Fact]
    public async Task MissingCredential_Answers401WithChallengeAndSkipsThePipeline()
    {
        var middleware = NewMiddleware();
        var context = NewContext();

        var invoked = false;
        await middleware.InvokeAsync(context, _ =>
        {
            invoked = true;
            return Task.CompletedTask;
        });

        Assert.False(invoked);
        AssertUnauthorized(context);
        Assert.Equal(
            """{"success":false,"error":"Authentication required.","code":"unauthorized"}""",
            ReadBody(context));
    }

    [Theory]
    [InlineData("Basic dXNlcjpwYXNz")]
    [InlineData("Bearer")]
    [InlineData("Bearer ")]
    [InlineData("Bearer token with spaces")]
    public async Task MalformedAuthorizationHeader_Answers401(string header)
    {
        var middleware = NewMiddleware();
        var context = NewContext(request => request.Headers.Authorization = header);

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        AssertUnauthorized(context);
    }

    [Fact]
    public async Task MultipleAuthorizationValues_Answers401()
    {
        var middleware = NewMiddleware();
        var context = NewContext(request =>
            request.Headers.Append("Authorization", $"Bearer {AdminToken}"));
        context.Request.Headers.Append("Authorization", $"Bearer {ServiceToken}");

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        AssertUnauthorized(context);
    }

    [Fact]
    public async Task QueryStringToken_Answers401_EvenWithAValidBearerHeader()
    {
        var middleware = NewMiddleware();
        var context = NewContext(request => request.Headers.Authorization = $"Bearer {AdminToken}");
        context.Request.QueryString = new QueryString("?access_token=leaked");

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        AssertUnauthorized(context);
    }

    [Fact]
    public async Task UnknownToken_Answers401()
    {
        var middleware = NewMiddleware();
        var context = NewContext(request => request.Headers.Authorization = "Bearer unknown-token");

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        AssertUnauthorized(context);
    }

    [Fact]
    public async Task ActiveDbCredential_ResolvesThePrincipalWithScopes()
    {
        var token = CredentialToken.Generate(CredentialKind.Pat);
        var db = new FakeCredentialStore();
        db.Add(new Credential(
            "cred_1",
            "agent-1",
            CredentialKind.Pat,
            CredentialToken.Hash(token),
            [Scope.Runner, Scope.Webhook],
            "spec",
            null,
            null,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        var middleware = NewMiddleware(db);
        var context = NewContext(request => request.Headers.Authorization = $"Bearer {token}");

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        var principal = Assert.IsType<MohistPrincipal>(context.Items[MohistPrincipal.HttpContextItemKey]);
        Assert.Equal("agent-1", principal.Id);
        Assert.Equal(PrincipalKind.Agent, principal.Kind);
        Assert.Equal([Scope.Runner, Scope.Webhook], principal.Scopes);
    }

    [Fact]
    public async Task RevokedDbCredential_Answers401()
    {
        var token = CredentialToken.Generate(CredentialKind.Pat);
        var db = new FakeCredentialStore();
        db.Add(new Credential(
            "cred_revoked",
            "agent-1",
            CredentialKind.Pat,
            CredentialToken.Hash(token),
            [Scope.Runner],
            "spec",
            null,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        var middleware = NewMiddleware(db);
        var context = NewContext(request => request.Headers.Authorization = $"Bearer {token}");

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        AssertUnauthorized(context);
    }

    [Fact]
    public async Task HealthRoute_IsReachableWithoutACredential()
    {
        var middleware = NewMiddleware();
        var context = NewContext(path: "/api/health");

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        Assert.Equal(HttpStatusCode.OK, (HttpStatusCode)context.Response.StatusCode);
        Assert.False(context.Items.ContainsKey(MohistPrincipal.HttpContextItemKey));
    }

    [Fact]
    public async Task NonAuthSurfacePath_SkipsAuthentication()
    {
        var middleware = NewMiddleware();
        var context = NewContext(path: "/favicon.ico");

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        Assert.Equal(HttpStatusCode.OK, (HttpStatusCode)context.Response.StatusCode);
        Assert.False(context.Items.ContainsKey(MohistPrincipal.HttpContextItemKey));
    }

    private static void AssertUnauthorized(HttpContext context)
    {
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Equal(
            "Bearer error=\"invalid_token\"",
            Assert.Single(context.Response.Headers.WWWAuthenticate));
    }

    private static string ReadBody(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static AuthResolutionMiddleware NewMiddleware(FakeCredentialStore? db = null)
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
        return new AuthResolutionMiddleware(loader, db ?? new FakeCredentialStore());
    }

    private static DefaultHttpContext NewContext(
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

    private sealed class FakeFileStore : IFileCredentialStore
    {
        public int CreateCount { get; private set; }

        public string LoadOrCreateDefault(string path) =>
            throw new InvalidOperationException("Tokens are always configured in these tests.");

        public string ReadExplicit(string path) =>
            throw new InvalidOperationException("Tokens are always configured in these tests.");
    }

    private sealed class FakeCredentialStore : ICredentialStore
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
    }
}
