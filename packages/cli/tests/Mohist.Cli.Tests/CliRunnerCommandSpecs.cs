using System.Net;
using System.Text.Json.Nodes;
using EnvironmentAbstractions.TestHelpers;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public class CliRunnerCommandSpecs
{
    private const string ActiveProjectId = "proj_test";

    private static (HttpClient http, RecordingHttpHandler handler, StringWriter output, StringWriter error, FakeFileSystem fileSystem, FakeCommandExecutor executor, MockEnvironmentVariableProvider env) SetupEnv(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder,
        string? activeProjectId = ActiveProjectId)
    {
        var handler = new RecordingHttpHandler(responder);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:3456") };
        var output = new StringWriter();
        var error = new StringWriter();
        var fileSystem = new FakeFileSystem();
        if (activeProjectId is not null)
        {
            fileSystem.AddFile(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mohist", "cli-state.json"),
                $"{{\"activeProjectId\":\"{activeProjectId}\"}}");
        }
        var executor = new FakeCommandExecutor();
        var env = new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false);
        return (http, handler, output, error, fileSystem, executor, env);
    }

    private static object Runner(string id, string kind, string hostname, string status, string scopeType, object? capacity, string? projectId = null, string? lastHeartbeatAt = "2026-06-20T12:00:00Z")
    {
        var scope = scopeType == "global"
            ? new { type = "global" }
            : (object)new { type = "project", projectId = projectId ?? "proj_test", projectName = "test-project" };

        return new
        {
            id,
            kind,
            hostname,
            scope,
            status,
            registeredAt = "2026-06-20T11:00:00Z",
            lastHeartbeatAt,
            connectionState = "connected",
            capabilities = new[] { "agent-run" },
            coderModels = new[] { "openai/gpt-5.5" },
            coderModelCount = 1,
            capacity,
            activeWork = (object?)null,
        };
    }

    private static object Capacity(int used, int total) => new { usedSlots = used, totalSlots = total };

    [Fact]
    public async Task RunnerHelp_ListsListSubcommand()
    {
        var (http, handler, output, error, fileSystem, executor, env) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new { success = true })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["runner", "--help"], output, error, fileSystem, executor, env);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("list", stdout, StringComparison.Ordinal);
        Assert.Contains("install", stdout, StringComparison.Ordinal);
        Assert.Contains("start", stdout, StringComparison.Ordinal);
        Assert.Contains("stop", stdout, StringComparison.Ordinal);
        Assert.Contains("status", stdout, StringComparison.Ordinal);
        Assert.Contains("logs", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunnerList_DefaultScope_ReturnsBothGlobalAndProjectRunners()
    {
        var runners = new[]
        {
            Runner("r-global", "host", "host-a", "idle", "global", Capacity(0, 2)),
            Runner("r-proj", "host", "host-b", "busy", "project", Capacity(1, 2)),
        };
        var (http, handler, output, error, fileSystem, executor, env) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { runners },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["runner", "list", "-o", "table"], output, error, fileSystem, executor, env);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal($"/api/projects/{ActiveProjectId}/runners", request.RequestUri?.PathAndQuery);
        var stdout = output.ToString();
        Assert.Contains("r-global", stdout, StringComparison.Ordinal);
        Assert.Contains("r-proj", stdout, StringComparison.Ordinal);
        Assert.Contains("global", stdout, StringComparison.Ordinal);
        Assert.Contains("test-project", stdout, StringComparison.Ordinal);
        Assert.Contains("host-a", stdout, StringComparison.Ordinal);
        Assert.Contains("host-b", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunnerList_ScopeGlobal_FiltersOutProjectRunners()
    {
        var runners = new[]
        {
            Runner("r-global", "host", "host-a", "idle", "global", Capacity(0, 2)),
            Runner("r-proj", "host", "host-b", "busy", "project", Capacity(1, 2)),
        };
        var (http, handler, output, error, fileSystem, executor, env) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { runners },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["runner", "list", "--scope", "global", "-o", "table"], output, error, fileSystem, executor, env);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("r-global", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("r-proj", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("test-project", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunnerList_ScopeProject_FiltersOutGlobalRunners()
    {
        var runners = new[]
        {
            Runner("r-global", "host", "host-a", "idle", "global", Capacity(0, 2)),
            Runner("r-proj", "host", "host-b", "busy", "project", Capacity(1, 2)),
        };
        var (http, handler, output, error, fileSystem, executor, env) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { runners },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["runner", "list", "--scope", "project", "-o", "table"], output, error, fileSystem, executor, env);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("r-proj", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("r-global", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("global", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunnerList_ProjectOverride_ResolvesProjectAndQueriesIt()
    {
        var (http, handler, output, error, fileSystem, executor, env) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { runners = Array.Empty<object>() },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["runner", "list", "--project", "proj_other", "-o", "table"], output, error, fileSystem, executor, env);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal($"/api/projects/proj_other/runners", request.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task RunnerList_RowIncludesAllRequiredColumns()
    {
        var runners = new[]
        {
            Runner("r-1", "agent", "host-x", "idle", "global", Capacity(0, 4), lastHeartbeatAt: DateTimeOffset.UtcNow.ToString("o")),
        };
        var (http, handler, output, error, fileSystem, executor, env) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { runners },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["runner", "list", "-o", "table"], output, error, fileSystem, executor, env);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("id", stdout, StringComparison.Ordinal);
        Assert.Contains("kind", stdout, StringComparison.Ordinal);
        Assert.Contains("status", stdout, StringComparison.Ordinal);
        Assert.Contains("scope", stdout, StringComparison.Ordinal);
        Assert.Contains("capacity", stdout, StringComparison.Ordinal);
        Assert.Contains("heartbeat", stdout, StringComparison.Ordinal);
        Assert.Contains("hostname", stdout, StringComparison.Ordinal);
        Assert.Contains("0/4 slots", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunnerList_MissingCapacity_ShowsDashRatherThanZeroZero()
    {
        var runners = new[]
        {
            Runner("r-offline", "host", "host-z", "offline", "project", capacity: null, projectId: "proj_test"),
        };
        var (http, handler, output, error, fileSystem, executor, env) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { runners },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["runner", "list", "-o", "table"], output, error, fileSystem, executor, env);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("-", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("0/0 slots", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunnerList_NoColorSet_EmitsNoAnsiEscapes()
    {
        var runners = new[]
        {
            Runner("r-1", "host", "host-a", "idle", "global", Capacity(0, 2)),
            Runner("r-2", "host", "host-b", "busy", "project", Capacity(1, 2)),
            Runner("r-3", "host", "host-c", "stale", "global", Capacity(0, 2)),
            Runner("r-4", "host", "host-d", "offline", "project", capacity: null),
        };
        var (http, handler, output, error, fileSystem, executor, env) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { runners },
            })));
        env["NO_COLOR"] = "1";

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["runner", "list", "-o", "table"], output, error, fileSystem, executor, env);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.DoesNotContain("\u001b[", stdout, StringComparison.Ordinal);
        Assert.Contains("idle", stdout, StringComparison.Ordinal);
        Assert.Contains("busy", stdout, StringComparison.Ordinal);
        Assert.Contains("stale", stdout, StringComparison.Ordinal);
        Assert.Contains("offline", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunnerList_ColorEnabled_EmitsDistinctAnsiEscapesPerStatus()
    {
        var handler = new RecordingHttpHandler((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    runners = new[]
                    {
                        Runner("r-idle", "host", "host-a", "idle", "global", Capacity(0, 2)),
                        Runner("r-busy", "host", "host-b", "busy", "project", Capacity(1, 2)),
                        Runner("r-stale", "host", "host-c", "stale", "global", Capacity(0, 2)),
                        Runner("r-offline", "host", "host-d", "offline", "project", capacity: null),
                    },
                },
            })));
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:3456") };
        var output = new StringWriter();
        var error = new StringWriter();
        var fileSystem = new FakeFileSystem();
        fileSystem.AddFile(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mohist", "cli-state.json"),
            $"{{\"activeProjectId\":\"{ActiveProjectId}\"}}");
        var executor = new FakeCommandExecutor();
        var api = new MohistCliApi(http, output, error, fileSystem, executor);

        var exitCode = await api.PrintRunnerListAsync(ActiveProjectId, MohistCliApi.RunnerScopeFilter.All, "table", colorEnabled: true);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("\u001b[32midle\u001b[0m", stdout, StringComparison.Ordinal);
        Assert.Contains("\u001b[34mbusy\u001b[0m", stdout, StringComparison.Ordinal);
        Assert.Contains("\u001b[33mstale\u001b[0m", stdout, StringComparison.Ordinal);
        Assert.Contains("\u001b[2moffline\u001b[0m", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunnerList_JsonOutput_EmitsValidJsonWithoutColorOrBorders()
    {
        var runners = new[]
        {
            Runner("r-1", "host", "host-a", "idle", "global", Capacity(0, 2)),
        };
        var (http, handler, output, error, fileSystem, executor, env) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { runners },
            })));
        env["NO_COLOR"] = null;

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["runner", "list", "-o", "json"], output, error, fileSystem, executor, env);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.DoesNotContain("\u001b[", stdout, StringComparison.Ordinal);
        var parsed = JsonNode.Parse(stdout.Trim());
        Assert.NotNull(parsed);
        var array = Assert.IsType<JsonArray>(parsed);
        Assert.Single(array);
        var first = array[0]!.AsObject();
        Assert.Equal("r-1", first["id"]?.GetValue<string>());
    }

    [Fact]
    public async Task RunnerList_EmptyResult_PrintsStartHintAndNoTable()
    {
        var (http, handler, output, error, fileSystem, executor, env) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { runners = Array.Empty<object>() },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["runner", "list", "-o", "table"], output, error, fileSystem, executor, env);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("No runners connected", stdout, StringComparison.Ordinal);
        Assert.Contains("npx mohist runner", stdout, StringComparison.Ordinal);
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
    }

    [Fact]
    public async Task RunnerList_ScopeProjectEmptyJson_EmitsEmptyArray()
    {
        var (http, handler, output, error, fileSystem, executor, env) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { runners = new[] { Runner("r-global-only", "host", "host-g", "idle", "global", Capacity(0, 1)) } },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["runner", "list", "--scope", "project", "-o", "json"], output, error, fileSystem, executor, env);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        var parsed = JsonNode.Parse(stdout.Trim()) as JsonArray;
        Assert.NotNull(parsed);
        Assert.Empty(parsed!);
    }

    [Fact]
    public async Task RunnerList_ServerDown_PrintsStandardErrorAndExitsNonZero()
    {
        var (http, handler, output, error, fileSystem, executor, env) = SetupEnv((_, _) =>
            throw new HttpRequestException("connection refused"));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["runner", "list"], output, error, fileSystem, executor, env);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Server is not running. Start with: mo server start", error.ToString());
    }

    [Fact]
    public async Task RunnerList_InvalidScope_FailsWithoutApiCall()
    {
        var (http, handler, output, error, fileSystem, executor, env) = SetupEnv((_, _) =>
            throw new InvalidOperationException("API must not be called when scope is invalid"));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["runner", "list", "--scope", "bogus"], output, error, fileSystem, executor, env);

        Assert.Equal(1, exitCode);
        Assert.Contains("--scope must be", error.ToString());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RunnerList_NoActiveProject_FailsWithStandardError()
    {
        var (http, handler, output, error, fileSystem, executor, env) = SetupEnv((_, _) =>
            throw new InvalidOperationException("API must not be called without an active project"), activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["runner", "list"], output, error, fileSystem, executor, env);

        Assert.Equal(1, exitCode);
        Assert.Contains("mo project use", error.ToString());
    }

    [Fact]
    public async Task RunnerList_ApiColorDisabled_EmitsNoAnsiEscapes()
    {
        var handler = new RecordingHttpHandler((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    runners = new[]
                    {
                        Runner("r-idle", "host", "host-a", "idle", "global", Capacity(0, 2)),
                    },
                },
            })));
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:3456") };
        var output = new StringWriter();
        var error = new StringWriter();
        var fileSystem = new FakeFileSystem();
        fileSystem.AddFile(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mohist", "cli-state.json"),
            $"{{\"activeProjectId\":\"{ActiveProjectId}\"}}");
        var executor = new FakeCommandExecutor();
        var api = new MohistCliApi(http, output, error, fileSystem, executor);

        var exitCode = await api.PrintRunnerListAsync(ActiveProjectId, MohistCliApi.RunnerScopeFilter.All, "table", colorEnabled: false);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.DoesNotContain("\u001b[", stdout, StringComparison.Ordinal);
        Assert.Contains("idle", stdout, StringComparison.Ordinal);
    }
}
