using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

/// <summary>
/// <c>mo auth token</c>: create prints the full token exactly once,
/// list shows only name + prefix, revoke hits the revoke endpoint —
/// and the server-side TTL/scope discipline is mirrored with local
/// usage validation before any request leaves the CLI.
/// </summary>
public sealed class CliAuthTokenCommandSpecs
{
    [Fact]
    public async Task Create_SendsTheRequest_AndPrintsTheTokenExactlyOnce()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(
            (_, _) => Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    id = "pat_1",
                    name = "ci",
                    scope = "readonly",
                    prefix = "moh_pat_AbCdEf12",
                    token = "moh_pat_AbCdEf12GhIjKlMnOpQrStUvWxYz0123456789-_ab",
                    expiresAt = "2026-10-28T00:00:00+00:00",
                    createdAt = "2026-07-30T00:00:00+00:00",
                },
            })));

        var exitCode = await RunAsync(http, ["auth", "token", "create", "--name", "ci", "--scope", "readonly", "--ttl", "720"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/auth/tokens", request.RequestUri!.AbsolutePath);
        var body = JsonNode.Parse(request.Body!)!;
        Assert.Equal("ci", body["name"]!.GetValue<string>());
        Assert.Equal("readonly", body["scope"]!.GetValue<string>());
        Assert.Equal(720, body["ttlHours"]!.GetValue<int>());

        var stdout = output.ToString();
        const string token = "moh_pat_AbCdEf12GhIjKlMnOpQrStUvWxYz0123456789-_ab";
        Assert.Single(Regex.Matches(stdout, Regex.Escape(token)));
    }

    [Fact]
    public async Task Create_DefaultsToOperatorScope_AndOmitsTtlForServerDefault()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(
            (_, _) => Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    id = "pat_1",
                    name = "ci",
                    scope = "operator",
                    prefix = "moh_pat_AbCdEf12",
                    token = "moh_pat_default",
                    expiresAt = "2026-10-28T00:00:00+00:00",
                    createdAt = "2026-07-30T00:00:00+00:00",
                },
            })));

        var exitCode = await RunAsync(http, ["auth", "token", "create", "--name", "ci"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var body = JsonNode.Parse(Assert.Single(handler.Requests).Body!)!;
        Assert.Equal("operator", body["scope"]!.GetValue<string>());
        Assert.Null(body["ttlHours"]);
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("")]
    public async Task Create_WithInvalidScope_IsRejectedLocally(string scope)
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exitCode = await RunAsync(
            http, ["auth", "token", "create", "--name", "ci", "--scope", scope], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("--scope must be 'operator' or 'readonly'", error.ToString());
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("8761")]
    public async Task Create_WithOutOfRangeTtl_IsRejectedLocally(string ttl)
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exitCode = await RunAsync(
            http, ["auth", "token", "create", "--name", "ci", "--ttl", ttl], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("--ttl must be between 1 and 8760 hours", error.ToString());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Create_WithoutName_IsRejectedLocally()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exitCode = await RunAsync(http, ["auth", "token", "create"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("--name is required", error.ToString());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Create_WithDuplicateName_ReportsTheConflict()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(
            (_, _) => Task.FromResult(RecordingHttpHandler.JsonError(
                "A PAT named 'ci' already exists; revoke it before reusing the name",
                "pat_name_in_use",
                HttpStatusCode.Conflict)));

        var exitCode = await RunAsync(
            http, ["auth", "token", "create", "--name", "ci"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("A PAT named 'ci' already exists", error.ToString());
    }

    [Fact]
    public async Task List_ShowsNamePrefixScopeAndStatus_ButNoFullToken()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(
            (_, _) => Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    tokens = new[]
                    {
                        new
                        {
                            id = "pat_1",
                            name = "ci",
                            prefix = "moh_pat_AbCdEf12",
                            scopes = new[] { "readonly" },
                            expiresAt = "2026-10-28T00:00:00+00:00",
                            revokedAt = (string?)null,
                            createdAt = "2026-07-30T00:00:00+00:00",
                        },
                        new
                        {
                            id = "pat_2",
                            name = "old",
                            prefix = "moh_pat_XxYyZz99",
                            scopes = new[] { "operator" },
                            expiresAt = "2026-08-01T00:00:00+00:00",
                            revokedAt = (string?)"2026-08-02T00:00:00+00:00",
                            createdAt = "2026-07-01T00:00:00+00:00",
                        },
                    },
                },
            })));

        var exitCode = await RunAsync(http, ["auth", "token", "list"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/api/auth/tokens", request.RequestUri!.AbsolutePath);

        var stdout = output.ToString();
        Assert.Contains("NAME", stdout, StringComparison.Ordinal);
        Assert.Contains("ci", stdout, StringComparison.Ordinal);
        Assert.Contains("moh_pat_AbCdEf12", stdout, StringComparison.Ordinal);
        Assert.Contains("moh_pat_XxYyZz99", stdout, StringComparison.Ordinal);
        Assert.Contains("readonly", stdout, StringComparison.Ordinal);
        Assert.Contains("active", stdout, StringComparison.Ordinal);
        Assert.Contains("revoked", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("token\":", stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task List_WhenEmpty_PrintsNoTokens()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(
            (_, _) => Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { tokens = Array.Empty<object>() },
            })));

        var exitCode = await RunAsync(http, ["auth", "token", "list"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Contains("No tokens", output.ToString());
    }

    [Fact]
    public async Task Revoke_SendsTheRevokeRequest()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(
            (_, _) => Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { name = "ci", revokedAt = "2026-08-02T00:00:00+00:00" },
            })));

        var exitCode = await RunAsync(http, ["auth", "token", "revoke", "ci"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/auth/tokens/ci/revoke", request.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Revoke_EscapesTheNameInThePath()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(
            (_, _) => Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { name = "ci bot", revokedAt = "2026-08-02T00:00:00+00:00" },
            })));

        var exitCode = await RunAsync(http, ["auth", "token", "revoke", "ci bot"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Equal("/api/auth/tokens/ci%20bot/revoke", Assert.Single(handler.Requests).RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Revoke_UnknownToken_ReportsNotFound()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(
            (_, _) => Task.FromResult(RecordingHttpHandler.JsonError(
                "No PAT named 'missing'",
                "not_found",
                HttpStatusCode.NotFound)));

        var exitCode = await RunAsync(http, ["auth", "token", "revoke", "missing"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("No PAT named 'missing'", error.ToString());
    }

    private static Task<int> RunAsync(
        HttpClient http,
        string[] args,
        StringWriter output,
        StringWriter error,
        FakeFileSystem fs,
        FakeCommandExecutor executor)
    {
        return MohistCliCommands.RunAsync(http, args, output, error, fs, executor);
    }
}
