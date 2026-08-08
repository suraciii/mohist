using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

// Converge install/update command surface to the verb-root only (T-001 of
// issue #388). The three resource-group paths `mo server install`,
// `mo server update`, `mo runner install` are removed because they were
// double-entry aliases for the verb-root `mo install <component>` /
// `mo update [component]`. The pre-existing `mo runner update` invariant
// (it never existed) is preserved and pinned here as a regression guard.
//
// See:
//   - openspec/changes/issue-388/specs/install-single-entry/spec.md
//   - openspec/changes/issue-388/specs/update-single-entry/spec.md
//   - openspec/changes/issue-388/design.md D1 (no-alias policy) and
//     D3 (explicit `mo runner update` invariant).
public class CliInstallUpdateSingleEntryTests
{
    [Fact]
    public async Task LegacyServerInstall_FailsToResolveAndTriggersNoInstallerCall()
    {
        var (handler, http, output, error, fs, executor, installer) = CliTestFactory.CreateInternal();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["server", "install"], output, error, fs, executor,
            installer: installer);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(installer.Calls);
        Assert.Empty(installer.InstallServerCalls);
    }

    [Fact]
    public async Task LegacyServerUpdate_FailsToResolveAndTriggersNoUpdaterCall()
    {
        var (handler, http, output, error, fs, executor, installer) = CliTestFactory.CreateInternal();
        var updater = new FakeSourceCodeUpdater();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["server", "update"], output, error, fs, executor,
            installer: installer, updater: updater);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(updater.Calls);
    }

    [Fact]
    public async Task LegacyRunnerInstall_FailsToResolveAndTriggersNoInstallerCall()
    {
        var (handler, http, output, error, fs, executor, installer) = CliTestFactory.CreateInternal();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["runner", "install"], output, error, fs, executor,
            installer: installer);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(installer.Calls);
        Assert.Empty(installer.InstallRunnerCalls);
    }

    [Fact]
    public async Task LegacyRunnerUpdate_FailsToResolveAndTriggersNoUpdaterCall()
    {
        // `mo runner update` was never a registered path. After this change
        // it still isn't; the explicit guard prevents a future "symmetry"
        // change from silently reintroducing it.
        var (handler, http, output, error, fs, executor, installer) = CliTestFactory.CreateInternal();
        var updater = new FakeSourceCodeUpdater();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["runner", "update"], output, error, fs, executor,
            installer: installer, updater: updater);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(updater.Calls);
    }

    [Fact]
    public async Task VerbRootInstallServer_StillInvokesInstallServerAsync()
    {
        var (handler, http, output, error, fs, executor, installer) = CliTestFactory.CreateInternal();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["install", "server", "--repo-root", "/repo", "--listen-url", "http://127.0.0.1:3456", "--dry-run", "--unit-dir", "/etc/systemd/system"],
            output, error, fs, executor,
            installer: installer);

        Assert.Equal(0, exitCode);
        var call = Assert.Single(installer.InstallServerCalls);
        Assert.Equal("/repo", call.RepoRoot);
        Assert.Equal("http://127.0.0.1:3456", call.ListenUrl);
        Assert.True(call.DryRun);
        Assert.Equal("/etc/systemd/system", call.UnitDir);
    }

    [Fact]
    public async Task VerbRootInstallServer_DefaultsAreUnchanged()
    {
        var (handler, http, output, error, fs, executor, installer) = CliTestFactory.CreateInternal();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["install", "server"], output, error, fs, executor,
            installer: installer);

        Assert.Equal(0, exitCode);
        var call = Assert.Single(installer.InstallServerCalls);
        Assert.Null(call.RepoRoot);
        Assert.Null(call.ListenUrl);
        Assert.False(call.DryRun);
        Assert.Null(call.UnitDir);
        Assert.Null(call.ServerUrl);
        Assert.Null(call.RunnerRoot);
    }

    [Fact]
    public async Task VerbRootInstallRunner_StillInvokesInstallRunnerAsync()
    {
        var (handler, http, output, error, fs, executor, installer) = CliTestFactory.CreateInternal();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["install", "runner", "--repo-root", "/repo", "--server-url", "http://127.0.0.1:3456", "--runner-root", "/var/lib/runner", "--dry-run", "--unit-dir", "/etc/systemd/system"],
            output, error, fs, executor,
            installer: installer);

        Assert.Equal(0, exitCode);
        var call = Assert.Single(installer.InstallRunnerCalls);
        Assert.Equal("/repo", call.RepoRoot);
        Assert.Equal("http://127.0.0.1:3456", call.ServerUrl);
        Assert.Equal("/var/lib/runner", call.RunnerRoot);
        Assert.True(call.DryRun);
        Assert.Equal("/etc/systemd/system", call.UnitDir);
    }

    [Fact]
    public async Task VerbRootInstallRunner_DefaultsAreUnchanged()
    {
        var (handler, http, output, error, fs, executor, installer) = CliTestFactory.CreateInternal(
            (_, _) => Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { token = "moh_enroll_defaults", expiresAt = "2026-08-21T00:15:00+00:00" },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["install", "runner"], output, error, fs, executor,
            installer: installer);

        Assert.Equal(0, exitCode);
        var call = Assert.Single(installer.InstallRunnerCalls);
        Assert.Null(call.RepoRoot);
        Assert.Null(call.ServerUrl);
        Assert.Null(call.RunnerRoot);
        Assert.False(call.DryRun);
        Assert.Null(call.UnitDir);
        Assert.Equal("moh_enroll_defaults", call.EnrollmentToken);
    }

    [Fact]
    public async Task VerbRootInstallRunner_FetchesAnEnrollmentToken_AndPassesItToTheInstaller()
    {
        var (handler, http, output, error, fs, executor, installer) = CliTestFactory.CreateInternal(
            (_, _) => Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { token = "moh_enroll_abc123", expiresAt = "2026-08-21T00:15:00+00:00" },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["install", "runner", "--repo-root", "/repo", "--server-url", "http://127.0.0.1:3456", "--runner-root", "/var/lib/runner", "--unit-dir", "/etc/systemd/system"],
            output, error, fs, executor,
            installer: installer);

        Assert.Equal(0, exitCode);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/runners/enrollment-tokens", request.RequestUri!.AbsolutePath);
        var call = Assert.Single(installer.InstallRunnerCalls);
        Assert.Equal("/repo", call.RepoRoot);
        Assert.Equal("http://127.0.0.1:3456", call.ServerUrl);
        Assert.Equal("/var/lib/runner", call.RunnerRoot);
        Assert.Equal("/etc/systemd/system", call.UnitDir);
        Assert.Equal("moh_enroll_abc123", call.EnrollmentToken);
    }

    [Fact]
    public async Task VerbRootInstallRunner_WhenTheServerIsUnavailable_FailsWithoutInstalling()
    {
        var (handler, http, output, error, fs, executor, installer) = CliTestFactory.CreateInternal(
            (_, _) => throw new HttpRequestException("connection refused"));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["install", "runner"], output, error, fs, executor,
            installer: installer);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(installer.InstallRunnerCalls);
        Assert.Contains("Server is not running", error.ToString());
    }

    [Fact]
    public async Task VerbRootInstallRunner_DryRun_StaysOffline()
    {
        var (handler, http, output, error, fs, executor, installer) = CliTestFactory.CreateInternal();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["install", "runner", "--dry-run"], output, error, fs, executor,
            installer: installer);

        Assert.Equal(0, exitCode);
        Assert.Empty(handler.Requests);
        var call = Assert.Single(installer.InstallRunnerCalls);
        Assert.True(call.DryRun);
        Assert.Null(call.EnrollmentToken);
    }

    [Fact]
    public async Task VerbRootUpdate_InvokesUpdateAllAsync()
    {
        var (handler, http, output, error, fs, executor, installer) = CliTestFactory.CreateInternal();
        var updater = new FakeSourceCodeUpdater();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["update", "--dry-run"], output, error, fs, executor,
            installer: installer, updater: updater);

        Assert.Equal(0, exitCode);
        Assert.Equal(new[] { nameof(FakeSourceCodeUpdater.UpdateAllAsync) }, updater.Calls);
    }

    [Fact]
    public async Task VerbRootUpdateCli_InvokesUpdateCliAsync()
    {
        var (handler, http, output, error, fs, executor, installer) = CliTestFactory.CreateInternal();
        var updater = new FakeSourceCodeUpdater();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["update", "cli"], output, error, fs, executor,
            installer: installer, updater: updater);

        Assert.Equal(0, exitCode);
        Assert.Equal(new[] { nameof(FakeSourceCodeUpdater.UpdateCliAsync) }, updater.Calls);
    }

    [Fact]
    public async Task VerbRootUpdateServer_InvokesUpdateServerAsync()
    {
        var (handler, http, output, error, fs, executor, installer) = CliTestFactory.CreateInternal();
        var updater = new FakeSourceCodeUpdater();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["update", "server"], output, error, fs, executor,
            installer: installer, updater: updater);

        Assert.Equal(0, exitCode);
        Assert.Equal(new[] { nameof(FakeSourceCodeUpdater.UpdateServerAsync) }, updater.Calls);
    }

    [Fact]
    public async Task VerbRootUpdateRunner_InvokesUpdateRunnerAsync()
    {
        var (handler, http, output, error, fs, executor, installer) = CliTestFactory.CreateInternal();
        var updater = new FakeSourceCodeUpdater();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["update", "runner"], output, error, fs, executor,
            installer: installer, updater: updater);

        Assert.Equal(0, exitCode);
        Assert.Equal(new[] { nameof(FakeSourceCodeUpdater.UpdateRunnerAsync) }, updater.Calls);
    }

    [Fact]
    public async Task VerbRootInstallSlack_InvokesInstallSlackAsync()
    {
        var (handler, http, output, error, fs, executor, installer) = CliTestFactory.CreateInternal();

        var exitCode = await MohistCliCommands.RunAsync(http, ["install", "slack"], output, error, fs, executor, installer: installer);

        Assert.Equal(0, exitCode);
        Assert.Single(installer.InstallSlackCalls);
    }

    [Theory]
    [InlineData("start", nameof(FakeServiceInstaller.StartSlackAsync))]
    [InlineData("stop", nameof(FakeServiceInstaller.StopSlackAsync))]
    [InlineData("restart", nameof(FakeServiceInstaller.RestartSlackAsync))]
    [InlineData("status", nameof(FakeServiceInstaller.StatusSlackAsync))]
    [InlineData("logs", nameof(FakeServiceInstaller.LogsSlackAsync))]
    [InlineData("uninstall", nameof(FakeServiceInstaller.UninstallSlackAsync))]
    public async Task ServiceSlackLifecycle_InvokesSlackInstaller(string verb, string expectedCall)
    {
        var (handler, http, output, error, fs, executor, installer) = CliTestFactory.CreateInternal();

        var exitCode = await MohistCliCommands.RunAsync(http, ["service", verb, "slack"], output, error, fs, executor, installer: installer);

        Assert.Equal(0, exitCode);
        Assert.Equal(new[] { expectedCall }, installer.Calls);
    }

    [Fact]
    public async Task VerbRootUpdateSlack_InvokesUpdateSlackAsync()
    {
        var (handler, http, output, error, fs, executor, installer) = CliTestFactory.CreateInternal();
        var updater = new FakeSourceCodeUpdater();

        var exitCode = await MohistCliCommands.RunAsync(http, ["update", "slack"], output, error, fs, executor, installer: installer, updater: updater);

        Assert.Equal(0, exitCode);
        Assert.Equal(new[] { nameof(FakeSourceCodeUpdater.UpdateSlackAsync) }, updater.Calls);
    }

    [Fact]
    public async Task ServerHelp_DoesNotAdvertiseInstallOrUpdate()
    {
        var (handler, http, output, error, fs, executor, installer) = CliTestFactory.CreateInternal();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["server", "--help"], output, error, fs, executor,
            installer: installer);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        // Issue #480 reshaped `server` to a read-only surface (status/health/info/logs);
        // local lifecycle verbs were moved to `mo service <verb> server`.
        foreach (var name in new[] { "status", "health", "info", "logs" })
            Assert.Contains($"\n  {name} ", stdout);
        Assert.DoesNotContain("\n  start ", stdout);
        Assert.DoesNotContain("\n  stop ", stdout);
        Assert.DoesNotContain("\n  restart ", stdout);
        Assert.DoesNotContain("\n  uninstall ", stdout);
        // Anchored on `\n  <name> ` to avoid false positives from substring
        // matches inside descriptions (e.g. "info" appearing in unrelated
        // prose).
        Assert.DoesNotContain("\n  install ", stdout);
        Assert.DoesNotContain("\n  update ", stdout);
    }

    [Fact]
    public async Task RunnerHelp_DoesNotAdvertiseInstallOrUpdate()
    {
        var (handler, http, output, error, fs, executor, installer) = CliTestFactory.CreateInternal();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["runner", "--help"], output, error, fs, executor,
            installer: installer);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        foreach (var name in new[] { "list", "view", "status" })
            Assert.Contains($"\n  {name} ", stdout);
        foreach (var name in new[] { "start", "stop", "restart", "service-status", "logs", "uninstall", "show" })
            Assert.DoesNotContain($"\n  {name} ", stdout);
        Assert.DoesNotContain("\n  install ", stdout);
        Assert.DoesNotContain("\n  update ", stdout);
    }

    [Fact]
    public async Task SurvivingServerSubcommands_StillResolve()
    {
        // Sanity check: deleting `install`/`update` from the server group
        // must not break any other subcommand. After issue #480 the server
        // group is read-only (status/health/info/logs); local lifecycle verbs
        // live under `mo service <verb> server`.
        var (handler, http, output, error, fs, executor, installer) = CliTestFactory.CreateInternal();

        foreach (var sub in new[] { "status", "health", "info", "logs" })
        {
            var exitCode = await MohistCliCommands.RunAsync(
                http, ["server", sub], output, error, fs, executor,
                installer: installer);
            Assert.Equal(0, exitCode);
        }
    }

    [Fact]
    public async Task SurvivingRunnerSubcommands_StillResolve()
    {
        var (handler, http, output, error, fs, executor, installer) = CliTestFactory.CreateInternal();
        var exitCode = await MohistCliCommands.RunAsync(
            http, ["runner", "--help"], output, error, fs, executor,
            installer: installer);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        foreach (var sub in new[] { "list", "view", "status" })
            Assert.Contains($"\n  {sub} ", stdout);
        Assert.Empty(handler.Requests);
    }
}
