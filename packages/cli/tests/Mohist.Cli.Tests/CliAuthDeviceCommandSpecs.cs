using System.Net;
using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

/// <summary>
/// <c>mo auth login / status / logout</c> (docs/auth.md "远程 CLI：设备
/// 授权登录"): login prints the grouped code and confirmation link, opens
/// the browser when possible, polls through authorization_pending and
/// slow_down, and stores the session in <c>~/.mohist/credentials.json</c>
/// (0600). Status reports the credential source for the current server;
/// logout revokes the server-side chain and clears the local entry.
/// </summary>
public sealed class CliAuthDeviceCommandSpecs
{
    private const string DeviceCodeGrantType = "urn:ietf:params:oauth:grant-type:device_code";
    private const string CredentialsPath = "/mohist-tests/user/.mohist/credentials.json";
    private const string AccessToken = "moh_session_0123456789abcdef0123456789abcdef";
    private const string RefreshToken = "moh_refresh_0123456789abcdef0123456789abcdef";
    private const string Server = "http://localhost:3456";

    [Fact]
    public async Task Login_PrintsTheCodeAndLink_OpensTheBrowser_Polls_AndStoresTheSession()
    {
        var pending = true;
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/auth/device/code")
            {
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new
                    {
                        deviceCode = "moh_device_secret",
                        userCode = "ABCDEFGH",
                        verificationUri = $"{Server}/device",
                        verificationUriComplete = $"{Server}/device?user_code=ABCDEFGH",
                        interval = 5,
                        expiresIn = 600,
                    },
                }, HttpStatusCode.Created));
            }
            if (request.RequestUri!.AbsolutePath == "/api/auth/token")
            {
                if (pending)
                {
                    pending = false;
                    return Task.FromResult(RecordingHttpHandler.JsonError(
                        "Authorization pending.", "authorization_pending", HttpStatusCode.BadRequest));
                }
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new
                    {
                        accessToken = AccessToken,
                        refreshToken = RefreshToken,
                        accessExpiresAt = "2026-01-01T01:00:00+00:00",
                        refreshExpiresAt = "2026-01-31T00:00:00+00:00",
                    },
                }));
            }
            return Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = new { } }));
        });

        var exitCode = await RunAsync(http, ["auth", "login"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains($"{Server}/device?user_code=ABCDEFGH", stdout, StringComparison.Ordinal);
        Assert.Contains("ABCD-EFGH", stdout, StringComparison.Ordinal);
        Assert.Contains($"Logged in to {Server}.", stdout, StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());

        var deviceCodeRequest = handler.Requests.Single(request =>
            request.RequestUri!.AbsolutePath == "/api/auth/device/code");
        Assert.False(string.IsNullOrEmpty(JsonNode.Parse(deviceCodeRequest.Body!)!["name"]!.GetValue<string>()));

        var pollRequests = handler.Requests.Where(request =>
            request.RequestUri!.AbsolutePath == "/api/auth/token").ToList();
        Assert.Equal(2, pollRequests.Count);
        var pollBody = JsonNode.Parse(pollRequests[0].Body!)!;
        Assert.Equal(DeviceCodeGrantType, pollBody["grant_type"]!.GetValue<string>());
        Assert.Equal("moh_device_secret", pollBody["device_code"]!.GetValue<string>());

        // The session is stored with the server key; the file carries the
        // rolling pair (0600 via user-only write).
        var stored = JsonNode.Parse(fs.ReadAllText(CredentialsPath))!;
        var entry = stored["servers"]![0]!;
        Assert.Equal(Server, entry["server"]!.GetValue<string>());
        Assert.Equal(AccessToken, entry["accessToken"]!.GetValue<string>());
        Assert.Equal(RefreshToken, entry["refreshToken"]!.GetValue<string>());

        // The browser-open attempt used the confirmation link.
        var browser = executor.Invocations.Single();
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("cmd.exe", browser.FileName);
            Assert.Contains("http://localhost:3456/device?user_code=ABCDEFGH", browser.Args);
        }
        else
        {
            Assert.Equal("xdg-open", browser.FileName);
            Assert.Equal($"{Server}/device?user_code=ABCDEFGH", browser.Args[0]);
        }
    }

    [Fact]
    public async Task Login_SlowDown_IncreasesThePollInterval()
    {
        var polls = 0;
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/auth/device/code")
            {
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new
                    {
                        deviceCode = "moh_device_secret",
                        userCode = "ABCDEFGH",
                        verificationUri = $"{Server}/device",
                        verificationUriComplete = $"{Server}/device?user_code=ABCDEFGH",
                        interval = 5,
                        expiresIn = 600,
                    },
                }, HttpStatusCode.Created));
            }
            if (request.RequestUri!.AbsolutePath == "/api/auth/token")
            {
                polls++;
                return polls switch
                {
                    1 => Task.FromResult(RecordingHttpHandler.JsonError("Authorization pending.", "authorization_pending", HttpStatusCode.BadRequest)),
                    2 => Task.FromResult(RecordingHttpHandler.JsonError("Polling too frequently.", "slow_down", HttpStatusCode.TooManyRequests)),
                    _ => Task.FromResult(RecordingHttpHandler.Json(new
                    {
                        success = true,
                        data = new
                        {
                            accessToken = AccessToken,
                            refreshToken = RefreshToken,
                            accessExpiresAt = "2026-01-01T01:00:00+00:00",
                            refreshExpiresAt = "2026-01-31T00:00:00+00:00",
                        },
                    })),
                };
            }
            return Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = new { } }));
        });

        var exitCode = await RunAsync(http, ["auth", "login"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Equal(3, polls);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task Login_ExpiredCode_FailsWithAReloginHint()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/auth/device/code")
            {
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new
                    {
                        deviceCode = "moh_device_secret",
                        userCode = "ABCDEFGH",
                        verificationUri = $"{Server}/device",
                        verificationUriComplete = $"{Server}/device?user_code=ABCDEFGH",
                        interval = 5,
                        expiresIn = 600,
                    },
                }, HttpStatusCode.Created));
            }
            return Task.FromResult(RecordingHttpHandler.JsonError(
                "The device authorization has expired.", "expired_token", HttpStatusCode.BadRequest));
        });

        var exitCode = await RunAsync(http, ["auth", "login"], output, error, fs, executor);

        Assert.Equal(1, exitCode);
        Assert.Contains("expired", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mo auth login", error.ToString(), StringComparison.Ordinal);
        Assert.False(fs.Exists(CredentialsPath));
    }

    [Fact]
    public async Task Login_Denied_ReportsTheDenial()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/auth/device/code")
            {
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new
                    {
                        deviceCode = "moh_device_secret",
                        userCode = "ABCDEFGH",
                        verificationUri = $"{Server}/device",
                        verificationUriComplete = $"{Server}/device?user_code=ABCDEFGH",
                        interval = 5,
                        expiresIn = 600,
                    },
                }, HttpStatusCode.Created));
            }
            return Task.FromResult(RecordingHttpHandler.JsonError(
                "The authorization was denied.", "access_denied", HttpStatusCode.BadRequest));
        });

        var exitCode = await RunAsync(http, ["auth", "login"], output, error, fs, executor);

        Assert.Equal(1, exitCode);
        Assert.Contains("denied", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Login_ReplacesAnExistingSessionForTheSameServer()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/auth/device/code")
            {
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new
                    {
                        deviceCode = "moh_device_secret",
                        userCode = "ABCDEFGH",
                        verificationUri = $"{Server}/device",
                        verificationUriComplete = $"{Server}/device?user_code=ABCDEFGH",
                        interval = 5,
                        expiresIn = 600,
                    },
                }, HttpStatusCode.Created));
            }
            return Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    accessToken = AccessToken,
                    refreshToken = RefreshToken,
                    accessExpiresAt = "2026-01-01T01:00:00+00:00",
                    refreshExpiresAt = "2026-01-31T00:00:00+00:00",
                },
            }));
        });
        fs.AddFile(CredentialsPath, $$"""
            {"servers":[{"server":"{{Server}}","accessToken":"moh_session_old","refreshToken":"moh_refresh_old","accessExpiresAt":"2025-01-01T00:00:00+00:00","refreshExpiresAt":"2025-02-01T00:00:00+00:00"}]}
            """);

        var exitCode = await RunAsync(http, ["auth", "login"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stored = JsonNode.Parse(fs.ReadAllText(CredentialsPath))!;
        var servers = stored["servers"]!.AsArray();
        Assert.Single(servers);
        Assert.Equal(AccessToken, servers[0]!["accessToken"]!.GetValue<string>());
    }

    [Fact]
    public async Task Status_WithAStoredSession_ReportsSourceAndLiveness()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(
            (_, _) => Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = new { } })));
        fs.AddFile(CredentialsPath, $$"""
            {"servers":[{"server":"{{Server}}","accessToken":"{{AccessToken}}","refreshToken":"{{RefreshToken}}","accessExpiresAt":"2026-01-01T01:00:00+00:00","refreshExpiresAt":"2026-01-31T00:00:00+00:00"}]}
            """);

        var exitCode = await RunAsync(http, ["auth", "status"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains($"Server: {Server}", stdout, StringComparison.Ordinal);
        Assert.Contains("credentials.json", stdout, StringComparison.Ordinal);
        Assert.Contains("Session: active", stdout, StringComparison.Ordinal);
        // The probe hit the session endpoint with the stored access token.
        Assert.Contains("/api/auth/session", handler.Requests.Select(request => request.RequestUri!.AbsolutePath));
        Assert.Equal(
            $"Bearer {AccessToken}",
            Assert.Single(handler.Requests.Single(request => request.RequestUri!.AbsolutePath == "/api/auth/session").Headers["Authorization"]));
    }

    [Fact]
    public async Task Status_WithExpiredSession_ReportsExpired()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(
            (_, _) => Task.FromResult(RecordingHttpHandler.JsonError(
                "Authentication required.", "unauthorized", HttpStatusCode.Unauthorized)));
        fs.AddFile(CredentialsPath, $$"""
            {"servers":[{"server":"{{Server}}","accessToken":"{{AccessToken}}","refreshToken":"{{RefreshToken}}","accessExpiresAt":"2026-01-01T01:00:00+00:00","refreshExpiresAt":"2026-01-31T00:00:00+00:00"}]}
            """);

        var exitCode = await RunAsync(http, ["auth", "status"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Contains("Session: expired", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_WithoutAnyCredential_SuggestsLogin()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exitCode = await RunAsync(http, ["auth", "status"], output, error, fs, executor);

        Assert.Equal(1, exitCode);
        var stdout = output.ToString();
        Assert.Contains("Not signed in", stdout, StringComparison.Ordinal);
        Assert.Contains("mo auth login", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Logout_RevokesOnTheServer_AndClearsTheLocalSession()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(
            (_, _) => Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = new { } })));
        fs.AddFile(CredentialsPath, $$"""
            {"servers":[{"server":"{{Server}}","accessToken":"{{AccessToken}}","refreshToken":"{{RefreshToken}}","accessExpiresAt":"2026-01-01T01:00:00+00:00","refreshExpiresAt":"2026-01-31T00:00:00+00:00"}]}
            """);

        var exitCode = await RunAsync(http, ["auth", "logout"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/api/auth/logout", request.RequestUri!.AbsolutePath);
        Assert.Equal(RefreshToken, JsonNode.Parse(request.Body!)!["refreshToken"]!.GetValue<string>());
        Assert.Contains($"Logged out of {Server}.", output.ToString(), StringComparison.Ordinal);
        var remaining = JsonNode.Parse(fs.ReadAllText(CredentialsPath))!;
        Assert.Empty(remaining["servers"]!.AsArray());
    }

    [Fact]
    public async Task Logout_WithoutALocalSession_IsANoOp()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exitCode = await RunAsync(http, ["auth", "logout"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("No local session", output.ToString(), StringComparison.Ordinal);
    }

    private static Task<int> RunAsync(
        HttpClient http,
        string[] args,
        StringWriter output,
        StringWriter error,
        FakeFileSystem fs,
        FakeCommandExecutor executor)
    {
        // Polling waits are released by the responder sequence, not the
        // wall clock (design/testing.md: no fake-time-released product
        // polling waits).
        return MohistCliCommands.RunAsync(
            http, args, output, error, fs, executor,
            pollWait: (_, _) => Task.CompletedTask);
    }
}
