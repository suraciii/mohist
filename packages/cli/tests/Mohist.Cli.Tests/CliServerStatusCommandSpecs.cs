using System.Net;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

// Specs for issue-480 T-003 — `mo server` reshaped to a read-only surface
// over the connected Mohist Server application (status, health, info,
// application logs). The local lifecycle verbs that used to live under
// `mo server start|stop|restart|status|logs|uninstall` have moved to the
// new `mo service <verb> server` group, and the overall Server status
// formerly surfaced as `mo project status` now lives at `mo server status`.
//
// Acceptance criteria covered:
//   - `mo server --help` lists only status, health, info, logs.
//   - `mo server status` issues GET /api/status?all=true and renders the
//     overall Server status.
//   - `mo project status` no longer resolves (exit non-zero, no HTTP).
//   - `mo server logs` issues GET /api/logs/tail and its help identifies
//     application logs and points to `mo service logs server` for
//     service-manager logs.
//   - `mo server start`, `mo server restart`, and `mo server uninstall`
//     exit non-zero and invoke no installer action.
//   - `mo server health` and `mo server info` are preserved as the only
//     two pre-existing read verbs; they continue to read the connected
//     application only.
public class CliServerStatusCommandSpecs
{
    [Fact]
    public async Task ServerHelp_ListsOnlyReadSubcommands()
    {
        var (handler, http, output, error, fs, executor, installer) = CliTestFactory.CreateInternal();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["server", "--help"], output, error, fs, executor,
            installer: installer);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        // The four surviving read subcommands must be listed; anchored on
        // `\n  <name> ` so prose mentions don't count.
        foreach (var name in new[] { "status", "health", "info", "logs" })
            Assert.Contains($"\n  {name} ", stdout);

        // Local lifecycle verbs must not be advertised — they live under
        // `mo service <verb> server` now.
        Assert.DoesNotContain("\n  start ", stdout);
        Assert.DoesNotContain("\n  stop ", stdout);
        Assert.DoesNotContain("\n  restart ", stdout);
        Assert.DoesNotContain("\n  uninstall ", stdout);
    }

    [Fact]
    public async Task ServerHelp_GroupDescriptionIdentifiesReadOnlySurface()
    {
        var (handler, http, output, error, fs, executor, installer) = CliTestFactory.CreateInternal();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["server", "--help"], output, error, fs, executor,
            installer: installer);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        // Group description must call out the read-only contract and point
        // the reader at the service group for lifecycle verbs.
        Assert.Contains("Connected Mohist Server application", stdout, StringComparison.Ordinal);
        Assert.Contains("mo service", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServerStatus_HitsStatusEndpointAndRendersResponse()
    {
        var (handler, http, output, error, fs, executor, installer) = CliTestFactory.CreateInternal(
            (_, _) => Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { running = true, capacity = 42 },
            })),
            activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["server", "status"], output, error, fs, executor,
            installer: installer);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/api/status?all=true", request.RequestUri?.PathAndQuery);
        Assert.Contains("\"running\": true", output.ToString());
        Assert.Contains("\"capacity\": 42", output.ToString());
        Assert.Empty(error.ToString());
        // No installer interaction: this is a pure HTTP read.
        Assert.Empty(installer.Calls);
    }

    [Fact]
    public async Task ServerStatus_HelpMentionsFormerProjectStatusPath()
    {
        var (handler, http, output, error, fs, executor, installer) = CliTestFactory.CreateInternal();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["server", "status", "--help"], output, error, fs, executor,
            installer: installer);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        // Disambiguation anchor: the help identifies this as the overall
        // Server status, formerly `mo project status`. This guards against
        // regressions where the command's description drifts back to a
        // local-unit semantic.
        Assert.Contains("project status", stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LegacyProjectStatus_NoLongerResolvesAndExitsNonZero()
    {
        var (handler, http, output, error, fs, executor, installer) = CliTestFactory.CreateInternal();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "status"], output, error, fs, executor,
            installer: installer);

        // Per D1 (no aliases retained) the former `mo project status` path
        // is removed outright — System.CommandLine surfaces a parse error
        // and the runner returns non-zero. No HTTP request must be issued.
        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ProjectHelp_NoLongerAdvertisesStatusSubcommand()
    {
        var (handler, http, output, error, fs, executor, installer) = CliTestFactory.CreateInternal();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "--help"], output, error, fs, executor,
            installer: installer);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        // `status` was relocated to `mo server status` and must no longer
        // appear as a peer of `list`/`create`/`view`/`use`/`delete`/`workflow`.
        Assert.Contains("list", stdout);
        Assert.Contains("create", stdout);
        Assert.Contains("view", stdout);
        Assert.Contains("use", stdout);
        Assert.Contains("delete", stdout);
        Assert.Contains("workflow", stdout);
        Assert.DoesNotContain("\n  status ", stdout);
        Assert.DoesNotContain("show", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServerLogs_HitsApplicationLogsEndpoint()
    {
        var (handler, http, output, error, fs, executor, installer) = CliTestFactory.CreateInternal(
            (_, _) => Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { lines = new[] { "2026-07-06T10:00:00Z INFO  server started", "2026-07-06T10:00:01Z INFO  listening on :8644" } },
            })),
            activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["server", "logs"], output, error, fs, executor,
            installer: installer);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/api/logs/tail", request.RequestUri?.PathAndQuery);
        Assert.Contains("server started", output.ToString());
        Assert.Contains("listening on :8644", output.ToString());
        Assert.Empty(error.ToString());
        // No installer interaction: this is a pure HTTP read of the
        // application's own log tail, not the service-manager journal.
        Assert.Empty(installer.Calls);
    }

    [Fact]
    public async Task ServerLogs_HelpIdentifiesApplicationLogsAndPointsToServiceLogs()
    {
        var (handler, http, output, error, fs, executor, installer) = CliTestFactory.CreateInternal();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["server", "logs", "--help"], output, error, fs, executor,
            installer: installer);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        // The command description must identify its output as application
        // logs (distinct from service-manager logs) and point readers to
        // `mo service logs server` for the service-manager counterpart.
        Assert.Contains("application logs", stdout, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mo service logs server", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServerHealth_HitsHealthEndpointAndSkipsInstaller()
    {
        var (handler, http, output, error, fs, executor, installer) = CliTestFactory.CreateInternal(
            (_, _) => Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { status = "ok" },
            })),
            activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["server", "health"], output, error, fs, executor,
            installer: installer);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/api/health", request.RequestUri?.PathAndQuery);
        Assert.Empty(installer.Calls);
    }

    [Fact]
    public async Task LegacyServerStart_FailsToResolveAndInvokesNoInstallerAction()
    {
        var (handler, http, output, error, fs, executor, installer) = CliTestFactory.CreateInternal();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["server", "start"], output, error, fs, executor,
            installer: installer);

        // Per issue #480: `mo server start` is removed. System.CommandLine
        // surfaces a parse error (exit non-zero). No installer call, no
        // HTTP request — both invariants matter.
        Assert.NotEqual(0, exitCode);
        Assert.Empty(installer.Calls);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task LegacyServerRestart_FailsToResolveAndInvokesNoInstallerAction()
    {
        var (handler, http, output, error, fs, executor, installer) = CliTestFactory.CreateInternal();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["server", "restart"], output, error, fs, executor,
            installer: installer);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(installer.Calls);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task LegacyServerUninstall_FailsToResolveAndInvokesNoInstallerAction()
    {
        var (handler, http, output, error, fs, executor, installer) = CliTestFactory.CreateInternal();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["server", "uninstall"], output, error, fs, executor,
            installer: installer);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(installer.Calls);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task LegacyServerStop_FailsToResolveAndInvokesNoInstallerAction()
    {
        // The full set of removed verbs is start/stop/restart/status/logs/uninstall;
        // `stop` is checked alongside the AC-named three so we don't regress
        // silently when restoring it later.
        var (handler, http, output, error, fs, executor, installer) = CliTestFactory.CreateInternal();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["server", "stop"], output, error, fs, executor,
            installer: installer);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(installer.Calls);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task LegacyServerLogsAsServiceManager_NoLongerInvokesInstaller()
    {
        // Pre-#480 `mo server logs` invoked `installer.LogsServerAsync`.
        // After T-003 it issues GET /api/logs/tail — the service-manager
        // variant has moved to `mo service logs server`. This regression
        // guard pins the new behavior.
        var (handler, http, output, error, fs, executor, installer) = CliTestFactory.CreateInternal(
            (_, _) => Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { lines = Array.Empty<string>() },
            })),
            activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["server", "logs"], output, error, fs, executor,
            installer: installer);

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain(nameof(FakeServiceInstaller.LogsServerAsync), installer.Calls);
    }
}
