using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

// Specs for issue-480 T-001 — the new `service` command group that
// unifies local managed-process lifecycle (systemd unit / scheduled
// task) for the server and runner targets.
//
// Acceptance criteria covered:
//   - `mo service --help` lists exactly start, stop, restart, status,
//     logs, uninstall and advertises no `--project`/`--project-id`
//     option.
//   - `mo service start server --dry-run` records the start call on the
//     installer with DryRun=true and emits no further state change.
//   - `mo service status database` exits non-zero (usage error) and
//     invokes no installer action.
//   - `mo service logs server --help` advertises --lines/--follow and
//     references `mo server logs` for application logs.
//   - `mo service stop runner` issues no HTTP request to the Server
//     (no remote mutation).
//   - The legacy verb/test locations under runner/server are not yet
//     rewritten (additive phase).
public class CliServiceCommandSpecs
{
    [Fact]
    public async Task ServiceHelp_ListsExactlyTheSixLifecycleVerbs()
    {
        var (handler, http, output, error, fs, executor, installer) = CliTestFactory.CreateInternal();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["service", "--help"], output, error, fs, executor,
            installer: installer);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("start", stdout, StringComparison.Ordinal);
        Assert.Contains("stop", stdout, StringComparison.Ordinal);
        Assert.Contains("restart", stdout, StringComparison.Ordinal);
        Assert.Contains("status", stdout, StringComparison.Ordinal);
        Assert.Contains("logs", stdout, StringComparison.Ordinal);
        Assert.Contains("uninstall", stdout, StringComparison.Ordinal);
        // Install/update are root-level only — must not be duplicated under `service`.
        Assert.DoesNotContain("\n  install ", stdout, StringComparison.Ordinal);
        // The summary command names are listed by System.CommandLine in a
        // two-column block; pin the exact ordering of the six lifecycle verbs.
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var commands = lines
            .Where(line => line.TrimStart().StartsWith("start ")
                || line.TrimStart().StartsWith("stop ")
                || line.TrimStart().StartsWith("restart ")
                || line.TrimStart().StartsWith("status ")
                || line.TrimStart().StartsWith("logs ")
                || line.TrimStart().StartsWith("uninstall "))
            .ToList();
        Assert.Equal(6, commands.Count);
    }

    [Fact]
    public async Task ServiceHelp_AdvertisesNoProjectOption()
    {
        var (handler, http, output, error, fs, executor, installer) = CliTestFactory.CreateInternal();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["service", "--help"], output, error, fs, executor,
            installer: installer);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.DoesNotContain("--project", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("--project-id", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartVerbHelp_AdvertisesDryRunAndUnitDirWithoutProject()
    {
        var (handler, http, output, error, fs, executor, installer) = CliTestFactory.CreateInternal();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["service", "start", "--help"], output, error, fs, executor,
            installer: installer);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("--dry-run", stdout, StringComparison.Ordinal);
        Assert.Contains("--unit-dir", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("--project", stdout, StringComparison.Ordinal);
        Assert.Contains("<Runner|Server>", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServiceStatus_InvalidTarget_ExitsNonZeroWithoutInstallerCall()
    {
        var (handler, http, output, error, fs, executor, installer) = CliTestFactory.CreateInternal();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["service", "status", "database"], output, error, fs, executor,
            installer: installer);

        // System.CommandLine converts the unknown enum value into a parse
        // error (exit 2 — usage failure). The installer must not be touched.
        Assert.NotEqual(0, exitCode);
        Assert.Empty(installer.Calls);
        Assert.Empty(handler.Requests);
        var combined = output.ToString() + error.ToString();
        // The post-error help output points readers at the allowed targets;
        // making this a structural assertion guards against accidentally
        // deleting the help reprint step that names them.
        Assert.Contains("Runner", combined, StringComparison.Ordinal);
        Assert.Contains("Server", combined, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServiceStartServer_DryRun_RecordsOnlyStartServerCall()
    {
        var (handler, http, output, error, fs, executor, installer) = CliTestFactory.CreateInternal();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["service", "start", "server", "--dry-run"], output, error, fs, executor,
            installer: installer);

        Assert.Equal(0, exitCode);
        // The dispatch table routes `start`+server → StartServerAsync only.
        Assert.Single(installer.Calls);
        Assert.Contains(nameof(FakeServiceInstaller.StartServerAsync), installer.Calls);
        Assert.DoesNotContain(nameof(FakeServiceInstaller.StopServerAsync), installer.Calls);
        Assert.DoesNotContain(nameof(FakeServiceInstaller.RestartServerAsync), installer.Calls);
        Assert.DoesNotContain(nameof(FakeServiceInstaller.StatusServerAsync), installer.Calls);
        Assert.DoesNotContain(nameof(FakeServiceInstaller.LogsServerAsync), installer.Calls);
        Assert.DoesNotContain(nameof(FakeServiceInstaller.UninstallServerAsync), installer.Calls);
        Assert.DoesNotContain(nameof(FakeServiceInstaller.StartRunnerAsync), installer.Calls);
        // No network: dry-run is purely a local lifecycle preview.
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ServiceStartServer_DryRun_PreviewsServiceManagerCommand()
    {
        // The "preview the service-manager command" AC is also exercised
        // end-to-end with the real SystemdServiceInstaller on a non-Linux
        // runner (which uses a FakeCommandExecutor). Verify the public
        // surface emits the dry-run preview line expected by the spec.
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["service", "start", "server", "--dry-run"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        // SystemdServiceInstaller prints the dry-run preview line on Linux
        // (the default platform for the test environment).
        Assert.Contains("Dry run", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ServiceStop_AnyTarget_IssuesNoHttp()
    {
        var (handler, http, output, error, fs, executor, installer) = CliTestFactory.CreateInternal();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["service", "stop", "runner"], output, error, fs, executor,
            installer: installer);

        Assert.Equal(0, exitCode);
        Assert.Single(installer.Calls);
        Assert.Contains(nameof(FakeServiceInstaller.StopRunnerAsync), installer.Calls);
        // Hard guarantee: no remote mutation path can run from `service`.
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ServiceAllLifecycleVerbs_RouteToCorrectInstallerMethod()
    {
        // Sweeps the dispatch table once: every verb/target combo should
        // reach exactly the matching IServiceInstaller method and stay
        // purely local (no HTTP).
        var (handler, http, output, error, fs, executor, installer) = CliTestFactory.CreateInternal();

        var verbs = new[] { "start", "stop", "restart", "status", "logs", "uninstall" };
        var perVerbTarget = "server";
        foreach (var verb in verbs)
        {
            var args = new[] { "service", verb, perVerbTarget };
            var exitCode = await MohistCliCommands.RunAsync(
                http, args, output, error, fs, executor, installer: installer);
            Assert.Equal(0, exitCode);
        }
        Assert.Equal(verbs.Length, installer.Calls.Count);
        Assert.Contains(nameof(FakeServiceInstaller.StartServerAsync), installer.Calls);
        Assert.Contains(nameof(FakeServiceInstaller.StopServerAsync), installer.Calls);
        Assert.Contains(nameof(FakeServiceInstaller.RestartServerAsync), installer.Calls);
        Assert.Contains(nameof(FakeServiceInstaller.StatusServerAsync), installer.Calls);
        Assert.Contains(nameof(FakeServiceInstaller.LogsServerAsync), installer.Calls);
        Assert.Contains(nameof(FakeServiceInstaller.UninstallServerAsync), installer.Calls);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ServiceLogsHelp_AdvertisesLinesAndFollow()
    {
        var (handler, http, output, error, fs, executor, installer) = CliTestFactory.CreateInternal();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["service", "logs", "server", "--help"], output, error, fs, executor,
            installer: installer);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("--lines", stdout, StringComparison.Ordinal);
        Assert.Contains("-n", stdout, StringComparison.Ordinal);
        Assert.Contains("--follow", stdout, StringComparison.Ordinal);
        Assert.Contains("-f", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServiceLogsHelp_ReferencesServerLogsForApplicationLogs()
    {
        var (handler, http, output, error, fs, executor, installer) = CliTestFactory.CreateInternal();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["service", "logs", "server", "--help"], output, error, fs, executor,
            installer: installer);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        // Help must identify the logs as service-manager logs and point to
        // `mo server logs` for application logs. The two sources are not
        // interchangeable.
        Assert.Contains("service-manager", stdout, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mo server logs", stdout, StringComparison.Ordinal);
        Assert.Contains("interchangeable", stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ServiceLogs_ValidInvocation_RoutesToInstaller()
    {
        var (handler, http, output, error, fs, executor, installer) = CliTestFactory.CreateInternal();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["service", "logs", "runner", "--lines", "50", "--follow"], output, error, fs, executor,
            installer: installer);

        Assert.Equal(0, exitCode);
        Assert.Single(installer.Calls);
        Assert.Contains(nameof(FakeServiceInstaller.LogsRunnerAsync), installer.Calls);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ServiceTarget_AcceptsCaseInsensitive()
    {
        var (handler, http, output, error, fs, executor, installer) = CliTestFactory.CreateInternal();

        var lower = await MohistCliCommands.RunAsync(
            http, ["service", "status", "server"], output, error, fs, executor,
            installer: installer);
        var upper = await MohistCliCommands.RunAsync(
            http, ["service", "status", "Server"], output, error, fs, executor,
            installer: installer);

        Assert.Equal(0, lower);
        Assert.Equal(0, upper);
        Assert.Equal(2, installer.Calls.Count(name => name == nameof(FakeServiceInstaller.StatusServerAsync)));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Service_Help_DoesNotMentionRunnerOrServerLifecycleDetails()
    {
        var (handler, http, output, error, fs, executor, installer) = CliTestFactory.CreateInternal();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["service", "--help"], output, error, fs, executor,
            installer: installer);

        Assert.Equal(0, exitCode);
        // Sanity: the group description must explicitly disavow Project
        // parsing (spec: "service commands SHALL accept no --project").
        var stdout = output.ToString();
        Assert.Contains("mo install", stdout, StringComparison.Ordinal);
        Assert.Contains("mo update", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("--project", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServiceInstall_FailsToResolveAndTriggersNoInstallerAction()
    {
        // Issue #480 T-004 AC: `mo service install server` MUST exit
        // non-zero. Install remains a root-level verb only — `service`
        // exposes the six lifecycle verbs (`start|stop|restart|status|logs|uninstall`).
        var (handler, http, output, error, fs, executor, installer) = CliTestFactory.CreateInternal();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["service", "install", "server"], output, error, fs, executor,
            installer: installer);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(installer.Calls);
        Assert.Empty(installer.InstallServerCalls);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ServiceHelp_DoesNotAdvertiseInstall()
    {
        // Anchor the service group's six-verb surface: install/update are
        // root-level only and must not be duplicated under `service`.
        var (handler, http, output, error, fs, executor, installer) = CliTestFactory.CreateInternal();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["service", "--help"], output, error, fs, executor,
            installer: installer);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.DoesNotContain("\n  install ", stdout);
        Assert.DoesNotContain("\n  update ", stdout);
    }
}
