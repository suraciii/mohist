using System.Text.Json.Nodes;
using EnvironmentAbstractions.TestHelpers;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public class CliSystemInfoCommandSpecs
{
    private static (HttpClient http, RecordingHttpHandler handler, StringWriter output, StringWriter error, FakeFileSystem fileSystem, FakeCommandExecutor executor, MockEnvironmentVariableProvider env) SetupEnv(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder,
        string? activeProjectId = null)
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(responder, activeProjectId);
        var env = new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false);
        return (http, handler, output, error, fs, executor, env);
    }

    private static object SystemInfoPayload(
        string version = "1.2.3",
        string gitHash = "abc123def456",
        string artifactDigest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
        string startedAt = "2026-06-20T11:00:00Z",
        string sourcePath = "/opt/mohist",
        string sourceBranch = "master",
        string sourceHead = "abc123def456",
        bool sourceDirty = false,
        string installMode = "local-source",
        string? serviceManager = "systemd",
        string? serverUnit = "mohist-server.service",
        string? runnerUnit = "mohist-runner.service",
        string? installReason = null,
        string updateStatus = "up-to-date",
        bool updateAvailable = false,
        string? updateReason = "Running server is up to date with source",
        string? serverStatus = "active",
        string? runnerStatus = "active",
        string dbPath = "/home/user/.mohist/mohist.db",
        string configPath = "/home/user/.mohist/config.jsonc",
        string logsPath = "/home/user/.mohist/logs",
        string opencodePath = "/home/user/.config/opencode")
    {
        return new
        {
            running = new { version, gitHash, artifactDigest, startedAt },
            source = new { path = sourcePath, branch = sourceBranch, head = sourceHead, dirty = sourceDirty },
            install = new
            {
                mode = installMode,
                serviceManager,
                serverUnit,
                runnerUnit,
                reason = installReason,
            },
            update = new { status = updateStatus, available = updateAvailable, reason = updateReason },
            services = new { server = serverStatus, runner = runnerStatus },
            paths = new
            {
                db = dbPath,
                config = configPath,
                logs = logsPath,
                opencode = opencodePath,
            },
        };
    }

    [Fact]
    public async Task ServerInfo_Table_RendersAllSixSections()
    {
        var (http, handler, output, error, fileSystem, executor, env) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = SystemInfoPayload(),
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["server", "info"], output, error, fileSystem, executor, env);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/api/system/info", request.RequestUri?.PathAndQuery);
        Assert.Empty(error.ToString());
        var stdout = output.ToString();
        Assert.Contains("Identity", stdout);
        Assert.Contains("Source", stdout);
        Assert.Contains("Install", stdout);
        Assert.Contains("Update", stdout);
        Assert.Contains("Services", stdout);
        Assert.Contains("Paths", stdout);
        Assert.Contains("version: 1.2.3", stdout);
        Assert.Contains("gitHash: abc123def456", stdout);
        Assert.Contains("artifactDigest: 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", stdout);
        Assert.Contains("startedAt: 2026-06-20T11:00:00Z", stdout);
        Assert.Contains("path: /opt/mohist", stdout);
        Assert.Contains("branch: master", stdout);
        Assert.Contains("dirty: false", stdout);
        Assert.Contains("mode: local-source", stdout);
        Assert.Contains("serviceManager: systemd", stdout);
        Assert.Contains("serverUnit: mohist-server.service", stdout);
        Assert.Contains("runnerUnit: mohist-runner.service", stdout);
        Assert.Contains("status: up-to-date", stdout);
        Assert.Contains("available: false", stdout);
        Assert.Contains("server: active", stdout);
        Assert.Contains("runner: active", stdout);
        Assert.Contains("db: /home/user/.mohist/mohist.db", stdout);
        Assert.Contains("config: /home/user/.mohist/config.jsonc", stdout);
        Assert.Contains("logs: /home/user/.mohist/logs", stdout);
        Assert.Contains("opencode: /home/user/.config/opencode", stdout);
    }

    [Fact]
    public async Task ServerInfo_Json_EmitsRawServerPayloadVerbatim()
    {
        var payload = SystemInfoPayload();
        var (http, handler, output, error, fileSystem, executor, env) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = payload,
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["server", "info", "--json", "running,source,install,update,services,paths"],
            output,
            error,
            fileSystem,
            executor,
            env);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal("/api/system/info", request.RequestUri?.PathAndQuery);
        var stdout = output.ToString();
        var parsed = JsonNode.Parse(stdout.Trim()) as JsonObject;
        Assert.NotNull(parsed);
        Assert.Equal("1.2.3", parsed!["running"]?["version"]?.GetValue<string>());
        Assert.Equal("abc123def456", parsed["running"]?["gitHash"]?.GetValue<string>());
        Assert.Equal("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", parsed["running"]?["artifactDigest"]?.GetValue<string>());
        Assert.Equal("/opt/mohist", parsed["source"]?["path"]?.GetValue<string>());
        Assert.Equal("master", parsed["source"]?["branch"]?.GetValue<string>());
        Assert.False(parsed["source"]?["dirty"]?.GetValue<bool>());
        Assert.Equal("local-source", parsed["install"]?["mode"]?.GetValue<string>());
        Assert.Equal("systemd", parsed["install"]?["serviceManager"]?.GetValue<string>());
        Assert.Equal("up-to-date", parsed["update"]?["status"]?.GetValue<string>());
        Assert.False(parsed["update"]?["available"]?.GetValue<bool>());
        Assert.Equal("active", parsed["services"]?["server"]?.GetValue<string>());
        Assert.Equal("active", parsed["services"]?["runner"]?.GetValue<string>());
        Assert.Equal("/home/user/.mohist/mohist.db", parsed["paths"]?["db"]?.GetValue<string>());
        Assert.Equal("/home/user/.mohist/config.jsonc", parsed["paths"]?["config"]?.GetValue<string>());
    }

    [Fact]
    public async Task ServerInfo_ServerDown_PrintsNoticeToStderrAndLocalSubsetToStdout()
    {
        var (http, handler, output, error, fileSystem, executor, env) = SetupEnv((_, _) =>
            throw new HttpRequestException("connection refused"));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["server", "info"], output, error, fileSystem, executor, env);

        Assert.Equal(0, exitCode);
        var stderr = error.ToString();
        Assert.Contains("Server is not running", stderr);
        Assert.Contains("mo service start server", stderr);
        var stdout = output.ToString();
        Assert.Contains("Server diagnostics unavailable", stdout);
        Assert.Contains("CLI (local)", stdout);
        Assert.Contains("version:", stdout);
    }

    [Fact]
    public async Task ServerInfo_ServerDownJson_PrintsPartialJsonWithCliVersion()
    {
        var (http, handler, output, error, fileSystem, executor, env) = SetupEnv((_, _) =>
            throw new HttpRequestException("connection refused"));

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["server", "info", "--json", "degraded,cliVersion"],
            output,
            error,
            fileSystem,
            executor,
            env);

        Assert.Equal(0, exitCode);
        Assert.Contains("Server is not running", error.ToString());
        var parsed = JsonNode.Parse(output.ToString().Trim()) as JsonObject;
        Assert.NotNull(parsed);
        Assert.True(parsed!["degraded"]?.GetValue<bool>());
        Assert.NotNull(parsed["cliVersion"]?.GetValue<string>());
        Assert.NotEmpty(parsed["cliVersion"]!.GetValue<string>()!);
    }

    [Fact]
    public async Task ServerInfo_Help_DisambiguatesFromMoInfo()
    {
        var (http, handler, output, error, fileSystem, executor, env) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new { success = true })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["server", "info", "--help"], output, error, fileSystem, executor, env);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("server-side system diagnostics", stdout, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mo info", stdout, StringComparison.Ordinal);
        Assert.Contains("CLI-local", stdout, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("JSON FIELDS", stdout, StringComparison.Ordinal);
        Assert.Contains("running", stdout, StringComparison.Ordinal);
        Assert.Contains("services", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServerInfo_HelpAndCanonicalReadbackExposeTheSameRuntimeResource()
    {
        var (helpHttp, helpHandler, helpOutput, helpError, helpFiles, helpExecutor, helpEnv) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new { success = true })));
        var helpExit = await MohistCliCommands.RunAsync(
            helpHttp,
            ["server", "info", "--help"],
            helpOutput,
            helpError,
            helpFiles,
            helpExecutor,
            helpEnv);

        Assert.Equal(0, helpExit);
        Assert.Contains("running", helpOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("services", helpOutput.ToString(), StringComparison.Ordinal);
        Assert.Empty(helpHandler.Requests);

        const string sourceHash = "0123456789abcdef0123456789abcdef01234567";
        const string artifactDigest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var (http, handler, output, error, fileSystem, executor, env) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = SystemInfoPayload(gitHash: sourceHash, artifactDigest: artifactDigest),
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["server", "info", "--json", "running,services"],
            output,
            error,
            fileSystem,
            executor,
            env);

        Assert.Equal(0, exitCode);
        Assert.Equal("/api/system/info", handler.Requests.Single().RequestUri?.PathAndQuery);
        var readback = Assert.IsType<JsonObject>(JsonNode.Parse(output.ToString().Trim()));
        Assert.Equal(sourceHash, readback["running"]?["gitHash"]?.GetValue<string>());
        Assert.Equal(artifactDigest, readback["running"]?["artifactDigest"]?.GetValue<string>());
        Assert.Equal("active", readback["services"]?["server"]?.GetValue<string>());
        Assert.Equal("active", readback["services"]?["runner"]?.GetValue<string>());
    }

    [Fact]
    public async Task Server_Help_ListsInfoSubcommand()
    {
        var (http, handler, output, error, fileSystem, executor, env) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new { success = true })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["server", "--help"], output, error, fileSystem, executor, env);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("info", stdout);
    }

    [Fact]
    public async Task LegacySystemInfo_NoLongerResolvesAndExitsNonZero()
    {
        var (http, handler, output, error, fileSystem, executor, env) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new { success = true })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["system", "info"], output, error, fileSystem, executor, env);

        // Per D1 (no aliases retained) the legacy `mo system info` path is
        // removed outright — System.CommandLine surfaces a parse error and
        // the runner returns non-zero. No HTTP request must be issued.
        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ServerInfo_Table_HandlesNullOptionalFieldsWithoutThrowing()
    {
        var payload = new
        {
            running = new { version = (string?)null, gitHash = (string?)null, startedAt = (string?)null },
            source = new { path = (string?)null, branch = (string?)null, head = (string?)null, dirty = false },
            install = new
            {
                mode = "binary",
                serviceManager = (string?)null,
                serverUnit = (string?)null,
                runnerUnit = (string?)null,
                reason = (string?)null,
            },
            update = new { status = "unsupported", available = false, reason = (string?)null },
            services = new { server = (string?)null, runner = (string?)null },
            paths = new
            {
                db = "/home/user/.mohist/mohist.db",
                config = (string?)null,
                logs = (string?)null,
                opencode = (string?)null,
            },
        };
        var (http, handler, output, error, fileSystem, executor, env) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = payload })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["server", "info"], output, error, fileSystem, executor, env);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("Identity", stdout);
        Assert.Contains("Source", stdout);
        Assert.Contains("Install", stdout);
        Assert.Contains("Update", stdout);
        Assert.Contains("Services", stdout);
        Assert.Contains("Paths", stdout);
        Assert.Contains("mode: binary", stdout);
        Assert.Contains("status: unsupported", stdout);
        Assert.Contains("db: /home/user/.mohist/mohist.db", stdout);
    }
}
