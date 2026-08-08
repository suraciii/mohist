using System.Net;
using System.Text.Json.Nodes;
using EnvironmentAbstractions.TestHelpers;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

/// <summary>
/// Session renewal (docs/auth.md "远程 CLI：设备授权登录"): a 401 on a
/// request that carried a credentials.json session rolls the pair
/// forward through the refresh endpoint and retries once, invisibly to
/// the user; when the refresh fails the command surfaces a re-login
/// hint. MOHIST_TOKEN is never refreshed — the env var is authoritative.
/// </summary>
public sealed class CliCredentialRefreshSpecs
{
    private const string OldAccess = "moh_session_oldoldoldoldoldoldoldoldoldoldoldold";
    private const string NewAccess = "moh_session_newnonewnonewnonewnonewnonewnonewn";
    private const string NewRefresh = "moh_refresh_newnonewnonewnonewnonewnonewnonewn";
    private const string Server = "http://localhost:3456";
    private const string CredentialsPath = "/mohist-tests/user/.mohist/credentials.json";

    [Fact]
    public async Task IssueList_On401_RollsTheSessionForward_AndRetries()
    {
        var projectsCalls = 0;
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((request, _) =>
        {
            if (IsProjectsPath(request))
            {
                projectsCalls++;
                return projectsCalls == 1
                    ? Task.FromResult(RecordingHttpHandler.JsonError(
                        "Authentication required.", "unauthorized", HttpStatusCode.Unauthorized))
                    : Task.FromResult(RecordingHttpHandler.Json(new
                    {
                        success = true,
                        data = Array.Empty<object>(),
                    }));
            }
            if (request.RequestUri!.AbsolutePath == "/api/auth/token")
            {
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new
                    {
                        accessToken = NewAccess,
                        refreshToken = NewRefresh,
                        accessExpiresAt = "2026-01-01T02:00:00+00:00",
                        refreshExpiresAt = "2026-02-01T00:00:00+00:00",
                    },
                }));
            }
            return Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = new { } }));
        });
        fs.AddFile(CredentialsPath, $$"""
            {"servers":[{"server":"{{Server}}","accessToken":"{{OldAccess}}","refreshToken":"moh_refresh_oldoldoldoldoldoldoldoldoldoldoldold","accessExpiresAt":"2025-01-01T00:00:00+00:00","refreshExpiresAt":"2026-01-31T00:00:00+00:00"}]}
            """);

        var exitCode = await RunIssueListAsync(http, output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var projectCalls = handler.Requests.Where(request => IsProjectsPath(request)).ToList();
        Assert.Equal(2, projectCalls.Count);
        Assert.Equal($"Bearer {OldAccess}", Assert.Single(projectCalls[0].Headers["Authorization"]));
        Assert.Equal($"Bearer {NewAccess}", Assert.Single(projectCalls[1].Headers["Authorization"]));

        // The refresh request carried the old refresh token.
        var refreshCall = Assert.Single(handler.Requests, request =>
            request.RequestUri!.AbsolutePath == "/api/auth/token");
        var body = JsonNode.Parse(refreshCall.Body!)!;
        Assert.Equal("refresh_token", body["grant_type"]!.GetValue<string>());
        Assert.Equal("moh_refresh_oldoldoldoldoldoldoldoldoldoldoldold", body["refresh_token"]!.GetValue<string>());

        // The local session was rolled forward too.
        var stored = JsonNode.Parse(fs.ReadAllText(CredentialsPath))!;
        var entry = stored["servers"]![0]!;
        Assert.Equal(NewAccess, entry["accessToken"]!.GetValue<string>());
        Assert.Equal(NewRefresh, entry["refreshToken"]!.GetValue<string>());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task IssueList_WhenTheRefreshFails_PrintsTheReloginHint()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((request, _) =>
        {
            if (IsProjectsPath(request))
            {
                return Task.FromResult(RecordingHttpHandler.JsonError(
                    "Authentication required.", "unauthorized", HttpStatusCode.Unauthorized));
            }
            return Task.FromResult(RecordingHttpHandler.JsonError(
                "The presented credential is invalid, expired or revoked.", "invalid_grant", HttpStatusCode.BadRequest));
        });
        fs.AddFile(CredentialsPath, $$"""
            {"servers":[{"server":"{{Server}}","accessToken":"{{OldAccess}}","refreshToken":"moh_refresh_oldoldoldoldoldoldoldoldoldoldoldold","accessExpiresAt":"2025-01-01T00:00:00+00:00","refreshExpiresAt":"2026-01-31T00:00:00+00:00"}]}
            """);

        var exitCode = await RunIssueListAsync(http, output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Session expired. Run 'mo auth login' to sign in again.", error.ToString(), StringComparison.Ordinal);
        // Exactly one projects attempt: the retry only happens after a
        // successful refresh.
        Assert.Single(handler.Requests, request => IsProjectsPath(request));
    }

    [Fact]
    public async Task MohistToken_IsNotRefreshedOn401()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((request, _) =>
        {
            if (IsProjectsPath(request))
            {
                return Task.FromResult(RecordingHttpHandler.JsonError(
                    "Authentication required.", "unauthorized", HttpStatusCode.Unauthorized));
            }
            return Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = new { } }));
        });
        var environment = new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false);
        environment["MOHIST_TOKEN"] = "env-token-0123456789abcdef0123456789";

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "list"], output, error, fs, executor, environment);

        Assert.NotEqual(0, exitCode);
        Assert.DoesNotContain(handler.Requests, request => request.RequestUri!.AbsolutePath == "/api/auth/token");
        Assert.DoesNotContain("mo auth login", error.ToString(), StringComparison.Ordinal);
    }

    private static bool IsProjectsPath(HttpRequestMessage request) =>
        request.RequestUri!.AbsolutePath.StartsWith("/api/projects", StringComparison.Ordinal);

    private static bool IsProjectsPath(CapturedRequest request) =>
        request.RequestUri!.AbsolutePath.StartsWith("/api/projects", StringComparison.Ordinal);

    private static Task<int> RunIssueListAsync(
        HttpClient http,
        StringWriter output,
        StringWriter error,
        FakeFileSystem fs,
        FakeCommandExecutor executor) =>
        MohistCliCommands.RunAsync(http, ["issue", "list"], output, error, fs, executor);
}
