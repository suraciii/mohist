using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EnvironmentAbstractions.TestHelpers;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public class CliRunnerCommandSpecs
{
    private const string ActiveProjectId = "proj_test";
    private static readonly DateTimeOffset FixedNow = new(2026, 6, 30, 0, 0, 0, TimeSpan.Zero);

    private static (HttpClient http, RecordingHttpHandler handler, StringWriter output, StringWriter error, FakeFileSystem fileSystem, FakeCommandExecutor executor, MockEnvironmentVariableProvider env) SetupEnv(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder,
        string? activeProjectId = ActiveProjectId)
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(responder, activeProjectId);
        var env = new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false);
        return (http, handler, output, error, fs, executor, env);
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
        Assert.DoesNotContain("\n  install ", stdout, StringComparison.Ordinal);
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
            http, ["runner", "list",], output, error, fileSystem, executor, env);

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
            http, ["runner", "list", "--scope", "global",], output, error, fileSystem, executor, env);

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
            http, ["runner", "list", "--scope", "project",], output, error, fileSystem, executor, env);

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
            http, ["runner", "list", "--project", "proj_other",], output, error, fileSystem, executor, env);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal($"/api/projects/proj_other/runners", request.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task RunnerList_RowIncludesAllRequiredColumns()
    {
        var runners = new[]
        {
            Runner("r-1", "agent", "host-x", "idle", "global", Capacity(0, 4), lastHeartbeatAt: FixedNow.ToString("o")),
        };
        var (http, handler, output, error, fileSystem, executor, env) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { runners },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["runner", "list",], output, error, fileSystem, executor, env);

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
            http, ["runner", "list",], output, error, fileSystem, executor, env);

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
            http, ["runner", "list",], output, error, fileSystem, executor, env);

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
            Path.Combine(CliTestFactory.UserHome, ".mohist", "cli-state.json"),
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
            http, ["runner", "list", "--json", "id"], output, error, fileSystem, executor, env);

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
            http, ["runner", "list",], output, error, fileSystem, executor, env);

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
            http, ["runner", "list", "--scope", "project", "--json", "id"], output, error, fileSystem, executor, env);

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
        Assert.Contains(MohistCliApi.ServerUnavailableMessage, error.ToString());
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
            Path.Combine(CliTestFactory.UserHome, ".mohist", "cli-state.json"),
            $"{{\"activeProjectId\":\"{ActiveProjectId}\"}}");
        var executor = new FakeCommandExecutor();
        var api = new MohistCliApi(http, output, error, fileSystem, executor);

        var exitCode = await api.PrintRunnerListAsync(ActiveProjectId, MohistCliApi.RunnerScopeFilter.All, "table", colorEnabled: false);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.DoesNotContain("\u001b[", stdout, StringComparison.Ordinal);
        Assert.Contains("idle", stdout, StringComparison.Ordinal);
    }

    private static object RunnerDetail(
        string id,
        string status,
        object[] activeWorks,
        string connectionState = "connected",
        string? lastHeartbeatAt = "2026-06-20T12:00:00Z",
        string? registeredAt = "2026-06-20T11:00:00Z",
        string? buildGitHash = "abcdef1234567890",
        string kind = "agent",
        string hostname = "host-a",
        string scopeType = "project",
        string? projectId = null,
        string[]? capabilities = null,
        string[]? coderModels = null,
        object? capacity = null)
    {
        var scope = scopeType == "global"
            ? (object)new { type = "global" }
            : new { type = "project", projectId = projectId ?? "proj_test", projectName = "test-project" };

        return new
        {
            id,
            kind,
            hostname,
            scope,
            status,
            registeredAt,
            lastHeartbeatAt,
            connectionState,
            capabilities = capabilities ?? new[] { "spec/*", "workspace-query" },
            coderModels = coderModels ?? new[] { "openai/gpt-5.5", "anthropic/claude-3" },
            coderModelCount = (coderModels ?? new[] { "openai/gpt-5.5", "anthropic/claude-3" }).Length,
            capacity,
            buildGitHash,
            activeWorks,
        };
    }

    private static object ActiveWork(
        string workId,
        string ownerKind,
        string ownerId,
        string workType,
        string? stage = "build",
        string? title = "Implement feature",
        object? issue = null)
    {
        return new
        {
            workId,
            ownerKind,
            ownerId,
            workType,
            stage,
            title,
            issue,
        };
    }

    private static object IssueRef(int issueNumber, string projectId = "proj_test") =>
        new
        {
            projectId,
            issueNumber,
        };

    [Fact]
    public async Task RunnerShow_BusyRunner_PrintsIdentityCapabilitiesActiveWorksAndHealth()
    {
        var works = new[]
        {
            ActiveWork("w-1", "workflow", "wf-1", "build", "implement", "Add feature", IssueRef(214)),
            ActiveWork("w-2", "workflow", "wf-2", "review", "review", "Review PR", IssueRef(213)),
        };
        var detail = RunnerDetail("r-busy", "busy", works,
            capacity: new { usedSlots = 2, totalSlots = 2 });
        var (http, handler, output, error, fileSystem, executor, env) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { runner = detail },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["runner", "show", "r-busy",], output, error, fileSystem, executor, env);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal($"/api/projects/{ActiveProjectId}/runners/r-busy", request.RequestUri?.PathAndQuery);
        var stdout = output.ToString();
        Assert.Contains("Identity", stdout, StringComparison.Ordinal);
        Assert.Contains("Capabilities", stdout, StringComparison.Ordinal);
        Assert.Contains("Active Works", stdout, StringComparison.Ordinal);
        Assert.Contains("Health", stdout, StringComparison.Ordinal);
        Assert.Contains("r-busy", stdout, StringComparison.Ordinal);
        Assert.Contains("agent", stdout, StringComparison.Ordinal);
        Assert.Contains("host-a", stdout, StringComparison.Ordinal);
        Assert.Contains("abcdef1234567890", stdout, StringComparison.Ordinal);
        Assert.Contains("spec/*", stdout, StringComparison.Ordinal);
        Assert.Contains("openai/gpt-5.5", stdout, StringComparison.Ordinal);
        Assert.Contains("maxWorkflowSlots: 2", stdout, StringComparison.Ordinal);
        Assert.Contains("capacity: 2/2 slots", stdout, StringComparison.Ordinal);
        Assert.Contains("[1]", stdout, StringComparison.Ordinal);
        Assert.Contains("[2]", stdout, StringComparison.Ordinal);
        Assert.Contains("w-1", stdout, StringComparison.Ordinal);
        Assert.Contains("w-2", stdout, StringComparison.Ordinal);
        Assert.Contains("Add feature", stdout, StringComparison.Ordinal);
        Assert.Contains("Review PR", stdout, StringComparison.Ordinal);
        Assert.Contains("214", stdout, StringComparison.Ordinal);
        Assert.Contains("213", stdout, StringComparison.Ordinal);
        Assert.Contains("busy", stdout, StringComparison.Ordinal);
        Assert.Contains("connected", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunnerShow_IdleRunner_PrintsDetailWithExplicitNoActiveWorksSection()
    {
        var detail = RunnerDetail("r-idle", "idle", Array.Empty<object>());
        var (http, handler, output, error, fileSystem, executor, env) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { runner = detail },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["runner", "show", "r-idle",], output, error, fileSystem, executor, env);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("Identity", stdout, StringComparison.Ordinal);
        Assert.Contains("Capabilities", stdout, StringComparison.Ordinal);
        Assert.Contains("Active Works", stdout, StringComparison.Ordinal);
        Assert.Contains("Health", stdout, StringComparison.Ordinal);
        Assert.Contains("r-idle", stdout, StringComparison.Ordinal);
        Assert.Contains("idle", stdout, StringComparison.Ordinal);
        Assert.Contains("(no active works)", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunnerShow_UnknownRunner_ReturnsNotFoundAndNonZeroExit()
    {
        var (http, handler, output, error, fileSystem, executor, env) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(
                new { success = false, error = "Runner 'r-ghost' not found", code = "not_found" },
                HttpStatusCode.NotFound)));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["runner", "show", "r-ghost"], output, error, fileSystem, executor, env);

        Assert.Equal(1, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal($"/api/projects/{ActiveProjectId}/runners/r-ghost", request.RequestUri?.PathAndQuery);
        var stderr = error.ToString();
        Assert.Contains("not found", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("r-ghost", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Identity", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunnerShow_ProjectOverride_QueriesThatProject()
    {
        var detail = RunnerDetail("r-x", "idle", Array.Empty<object>());
        var (http, handler, output, error, fileSystem, executor, env) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { runner = detail },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["runner", "show", "r-x", "--project", "proj_other"], output, error, fileSystem, executor, env);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal($"/api/projects/proj_other/runners/r-x", request.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task RunnerShow_NoActiveProject_FailsWithStandardError()
    {
        var (http, handler, output, error, fileSystem, executor, env) = SetupEnv((_, _) =>
            throw new InvalidOperationException("API must not be called without an active project"), activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["runner", "show", "r-1"], output, error, fileSystem, executor, env);

        Assert.Equal(1, exitCode);
        Assert.Contains("mo project use", error.ToString());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RunnerShow_SelectedJson_ProjectsRequestedFields()
    {
        var works = new[]
        {
            ActiveWork("w-1", "workflow", "wf-1", "build", "implement", "Add feature", IssueRef(214)),
        };
        var detail = RunnerDetail("r-busy", "busy", works);
        var (http, handler, output, error, fileSystem, executor, env) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { runner = detail },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["runner", "show", "r-busy", "--json", "id,activeWorks"], output, error, fileSystem, executor, env);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        var parsed = JsonNode.Parse(stdout.Trim()) as JsonObject;
        Assert.NotNull(parsed);
        Assert.Equal("r-busy", parsed!["id"]?.GetValue<string>());
        var worksArray = parsed["activeWorks"] as JsonArray;
        Assert.NotNull(worksArray);
        Assert.Single(worksArray!);
        Assert.Equal("w-1", worksArray![0]!["workId"]?.GetValue<string>());
        Assert.Equal(214, worksArray![0]!["issue"]?["issueNumber"]?.GetValue<int>());
    }

    [Fact]
    public async Task RunnerShow_HelpText_ListsShowAndExistingSubcommands()
    {
        var (http, handler, output, error, fileSystem, executor, env) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new { success = true })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["runner", "--help"], output, error, fileSystem, executor, env);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("show", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("\n  install ", stdout, StringComparison.Ordinal);
        Assert.Contains("start", stdout, StringComparison.Ordinal);
        Assert.Contains("stop", stdout, StringComparison.Ordinal);
        Assert.Contains("status", stdout, StringComparison.Ordinal);
        Assert.Contains("logs", stdout, StringComparison.Ordinal);
        Assert.Contains("uninstall", stdout, StringComparison.Ordinal);
        Assert.Contains("list", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunnerShow_WorkWithoutIssue_RendersWorkWithoutIssueLink()
    {
        var works = new[]
        {
            ActiveWork("w-no-issue", "workflow", "wf-no-issue", "build", "implement", "Build feature", issue: null),
        };
        var detail = RunnerDetail("r-x", "busy", works);
        var (http, handler, output, error, fileSystem, executor, env) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { runner = detail },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["runner", "show", "r-x",], output, error, fileSystem, executor, env);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("w-no-issue", stdout, StringComparison.Ordinal);
        Assert.Contains("Build feature", stdout, StringComparison.Ordinal);
        Assert.Contains("wf-no-issue", stdout, StringComparison.Ordinal);
        var lines = stdout.Split('\n');
        var workSection = lines.SkipWhile(l => !l.Contains("Active Works", StringComparison.Ordinal))
            .TakeWhile(l => !l.StartsWith("Health", StringComparison.Ordinal));
        Assert.DoesNotContain(workSection, l => l.Contains("issue:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunnerShow_TableOutput_ReadsMaxSlotsFromCapacityTotalSlots()
    {
        var detail = RunnerDetail(
            "r-capacity",
            "idle",
            Array.Empty<object>(),
            capacity: new { usedSlots = 0, totalSlots = 7 });
        var (http, handler, output, error, fileSystem, executor, env) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { runner = detail },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["runner", "show", "r-capacity",], output, error, fileSystem, executor, env);

        Assert.Equal(0, exitCode);
        Assert.Equal($"/api/projects/{ActiveProjectId}/runners/r-capacity", handler.Requests.Single().RequestUri?.PathAndQuery);
        var stdout = output.ToString();
        Assert.Contains("maxWorkflowSlots: 7", stdout, StringComparison.Ordinal);
        Assert.Contains("capacity: 0/7 slots", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("maxWorkflowSlots: (unknown)", stdout, StringComparison.Ordinal);
    }

    private static object RunnerStatusEntry(
        string id,
        string hostname,
        object? capacity,
        string scopeType = "global",
        string? projectId = null,
        string? lastHeartbeatAt = null)
    {
        var scope = scopeType == "global"
            ? (object)new { type = "global" }
            : new { type = "project", projectId = projectId ?? "proj_test", projectName = "test-project" };

        return new
        {
            id,
            kind = "host",
            hostname,
            scope,
            status = capacity is null ? "offline" : "online",
            registeredAt = "2026-06-20T11:00:00Z",
            lastHeartbeatAt = lastHeartbeatAt ?? FixedNow.AddSeconds(-5).ToString("o"),
            connectionState = capacity is null ? "disconnected" : "connected",
            capabilities = new[] { "agent-run" },
            coderModels = new[] { "openai/gpt-5.5" },
            coderModelCount = 1,
            capacity,
            activeWorks = (object?)null,
        };
    }

    [Fact]
    public async Task RunnerStatus_Table_RendersThreeColumnSummaryWithIdleBusyStates()
    {
        var runners = new[]
        {
            RunnerStatusEntry("r-idle", "host-a", Capacity(0, 2)),
            RunnerStatusEntry("r-busy", "host-b", Capacity(1, 2)),
        };
        var (http, handler, output, error, fileSystem, executor, env) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { runners },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["runner", "status"], output, error, fileSystem, executor, env);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"/api/projects/{ActiveProjectId}/runners", request.RequestUri?.PathAndQuery);
        var stdout = output.ToString();
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("id", lines[0]);
        Assert.Contains("heartbeat", lines[0]);
        Assert.Contains("state", lines[0]);
        Assert.Contains("r-idle", lines[1]);
        Assert.Contains("idle", lines[1]);
        Assert.Contains("r-busy", lines[2]);
        Assert.Contains("busy", lines[2]);
        Assert.DoesNotContain("kind", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("scope", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("hostname", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunnerStatus_Table_UnknownCapacityShowsUnknownState()
    {
        var runners = new[]
        {
            RunnerStatusEntry("r-offline", "host-z", capacity: null, scopeType: "project"),
        };
        var (http, handler, output, error, fileSystem, executor, env) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { runners },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["runner", "status"], output, error, fileSystem, executor, env);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("r-offline", stdout, StringComparison.Ordinal);
        Assert.Contains("unknown", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunnerStatus_SelectedJson_EmitsRunnerCollection()
    {
        var runners = new[]
        {
            RunnerStatusEntry("r-idle", "host-a", Capacity(0, 2)),
            RunnerStatusEntry("r-busy", "host-b", Capacity(2, 2)),
        };
        var (http, handler, output, error, fileSystem, executor, env) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { runners },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["runner", "status", "--json", "id,capacity"], output, error, fileSystem, executor, env);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal($"/api/projects/{ActiveProjectId}/runners", request.RequestUri?.PathAndQuery);
        var parsed = JsonNode.Parse(output.ToString().Trim()) as JsonArray;
        Assert.NotNull(parsed);
        Assert.Equal(2, parsed!.Count);
        var first = parsed[0]!.AsObject();
        Assert.Equal("r-idle", first["id"]?.GetValue<string>());
        var capacity = first["capacity"]!.AsObject();
        Assert.Equal(0, capacity["usedSlots"]?.GetValue<int>());
        Assert.Equal(2, capacity["totalSlots"]?.GetValue<int>());
    }

    [Fact]
    public async Task RunnerStatus_EmptyList_PrintsNoRunnersConnectedAndExitsZero()
    {
        var (http, handler, output, error, fileSystem, executor, env) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { runners = Array.Empty<object>() },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["runner", "status"], output, error, fileSystem, executor, env);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("No runners connected", stdout, StringComparison.Ordinal);
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
    }

    [Fact]
    public async Task RunnerStatus_EmptyListSelectedJson_EmitsEmptyArray()
    {
        var (http, handler, output, error, fileSystem, executor, env) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { runners = Array.Empty<object>() },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["runner", "status", "--json", "id"], output, error, fileSystem, executor, env);

        Assert.Equal(0, exitCode);
        var parsed = JsonNode.Parse(output.ToString().Trim()) as JsonArray;
        Assert.NotNull(parsed);
        Assert.Empty(parsed!);
    }

    [Fact]
    public async Task RunnerStatus_Table_TreatsDecimalZeroUsedSlotsAsIdle()
    {
        var (http, handler, output, error, fileSystem, executor, env) = SetupEnv((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {"success":true,"data":{"runners":[{"id":"r-decimal-zero","kind":"host","hostname":"host-a","scope":{"type":"global"},"status":"online","registeredAt":"2026-06-20T11:00:00Z","lastHeartbeatAt":"2026-06-20T12:00:00Z","connectionState":"connected","capabilities":["agent-run"],"coderModels":["openai/gpt-5.5"],"coderModelCount":1,"capacity":{"usedSlots":0.0,"totalSlots":2.0},"activeWorks":null}]}}
                    """,
                    Encoding.UTF8,
                    "application/json"),
            }));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["runner", "status"], output, error, fileSystem, executor, env);

        Assert.Equal(0, exitCode);
        Assert.Single(handler.Requests);
        var stdout = output.ToString();
        Assert.Contains("r-decimal-zero", stdout, StringComparison.Ordinal);
        Assert.Contains("idle", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("busy", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunnerStatus_NoActiveProject_FailsWithStandardError()
    {
        var (http, handler, output, error, fileSystem, executor, env) = SetupEnv(
            (_, _) => throw new InvalidOperationException("API must not be called without an active project"),
            activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["runner", "status"], output, error, fileSystem, executor, env);

        Assert.Equal(1, exitCode);
        Assert.Contains("mo project use", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RunnerStatus_ExplicitProjectFlag_ResolvesAndQueriesIt()
    {
        var runners = new[]
        {
            RunnerStatusEntry("r-idle", "host-a", Capacity(0, 2)),
        };
        var (http, handler, output, error, fileSystem, executor, env) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { runners },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["runner", "status", "--project", "proj_other"], output, error, fileSystem, executor, env);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal("/api/projects/proj_other/runners", request.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task RunnerStatus_ServerDown_PrintsStandardErrorAndExitsNonZero()
    {
        var (http, handler, output, error, fileSystem, executor, env) = SetupEnv((_, _) =>
            throw new HttpRequestException("connection refused"));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["runner", "status"], output, error, fileSystem, executor, env);

        Assert.NotEqual(0, exitCode);
        Assert.Contains(MohistCliApi.ServerUnavailableMessage, error.ToString());
    }

    [Fact]
    public async Task RunnerServiceStatus_DryRun_InvokesSameInstallerStatusAction()
    {
        var (http, handler, output, error, fileSystem, executor, env) = SetupEnv((_, _) =>
            throw new InvalidOperationException("service-status must not call the HTTP API"));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["runner", "service-status", "--dry-run"], output, error, fileSystem, executor, env);

        Assert.Equal(0, exitCode);
        Assert.Empty(handler.Requests);
        var stdout = output.ToString();
        Assert.Contains("Dry run: systemctl", stdout, StringComparison.Ordinal);
        Assert.Contains("status", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunnerServiceStatus_Help_ListsServiceStatusWithSameOptions()
    {
        var (http, handler, output, error, fileSystem, executor, env) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new { success = true })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["runner", "service-status", "--help"], output, error, fileSystem, executor, env);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("Show runner managed service lifecycle status", stdout, StringComparison.Ordinal);
        Assert.Contains("--dry-run", stdout, StringComparison.Ordinal);
        Assert.Contains("--unit-dir", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunnerStatus_Help_ListsFocusedOnlineSummary()
    {
        var (http, handler, output, error, fileSystem, executor, env) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new { success = true })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["runner", "status", "--help"], output, error, fileSystem, executor, env);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("online", stdout, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("idle", stdout, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("busy", stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunnerHelp_ListsBothStatusAndServiceStatusVerbsDistinctly()
    {
        var (http, handler, output, error, fileSystem, executor, env) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new { success = true })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["runner", "--help"], output, error, fileSystem, executor, env);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("service-status", stdout, StringComparison.Ordinal);
        Assert.Contains("status", stdout, StringComparison.Ordinal);
        Assert.Contains("Show online runner summary", stdout, StringComparison.Ordinal);
        Assert.Contains("Show runner managed service lifecycle status", stdout, StringComparison.Ordinal);
        var statusIdx = stdout.IndexOf("status", StringComparison.Ordinal);
        var serviceStatusIdx = stdout.IndexOf("service-status", StringComparison.Ordinal);
        Assert.True(statusIdx >= 0);
        Assert.True(serviceStatusIdx >= 0);
        Assert.NotEqual(statusIdx, serviceStatusIdx);
    }
}
