using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;

namespace Mohist.Cli;

internal static class UpdateCommands
{
    public static Command Build(IServiceProvider provider)
    {
        var update = new Command("update", "Update mohist components from source");
        var updater = provider.GetRequiredService<SourceCodeUpdater>();
        var repoRootOpt = new Option<string?>("--repo-root") { Description = "Repository root path" };
        var cliPathOpt = new Option<string?>("--cli-path") { Description = "mo executable path" };
        var dryRunOpt = MohistCliCommands.DryRunOption();

        update.Options.Add(repoRootOpt);
        update.Options.Add(cliPathOpt);
        update.Options.Add(dryRunOpt);
        update.SetAction(async (ctx, token) =>
        {
            var repoRoot = ctx.GetValue(repoRootOpt);
            var cliPath = ctx.GetValue(cliPathOpt);
            var dryRun = ctx.GetValue(dryRunOpt);
            return await updater.UpdateAllAsync(repoRoot, dryRun, cliPath, token);
        });

        update.Subcommands.Add(BuildCliUpdate(updater));
        update.Subcommands.Add(BuildServerUpdate(updater));
        update.Subcommands.Add(BuildRunnerUpdate(updater));

        return update;
    }

    private static Command BuildCliUpdate(SourceCodeUpdater updater)
    {
        var cmd = new Command("cli", "Update mo CLI from source");
        var repoRootOpt = new Option<string?>("--repo-root") { Description = "Repository root path" };
        var cliPathOpt = new Option<string?>("--cli-path") { Description = "mo executable path" };
        var dryRunOpt = MohistCliCommands.DryRunOption();
        cmd.Options.Add(repoRootOpt);
        cmd.Options.Add(cliPathOpt);
        cmd.Options.Add(dryRunOpt);
        cmd.SetAction(async (ctx, token) =>
        {
            var dryRun = ctx.GetValue(dryRunOpt);
            var repoRoot = ctx.GetValue(repoRootOpt);
            var cliPath = ctx.GetValue(cliPathOpt);
            return await updater.UpdateCliAsync(repoRoot, dryRun, cliPath, token);
        });
        return cmd;
    }

    private static Command BuildServerUpdate(SourceCodeUpdater updater)
    {
        var cmd = new Command("server", "Update server from source");
        var repoRootOpt = new Option<string?>("--repo-root") { Description = "Repository root path" };
        var dryRunOpt = MohistCliCommands.DryRunOption();
        cmd.Options.Add(repoRootOpt);
        cmd.Options.Add(dryRunOpt);
        cmd.SetAction(async (ctx, token) =>
        {
            var dryRun = ctx.GetValue(dryRunOpt);
            var repoRoot = ctx.GetValue(repoRootOpt);
            return await updater.UpdateServerAsync(repoRoot, dryRun, token);
        });
        return cmd;
    }

    private static Command BuildRunnerUpdate(SourceCodeUpdater updater)
    {
        var cmd = new Command("runner", "Update runner from source");
        var repoRootOpt = new Option<string?>("--repo-root") { Description = "Repository root path" };
        var dryRunOpt = MohistCliCommands.DryRunOption();
        cmd.Options.Add(repoRootOpt);
        cmd.Options.Add(dryRunOpt);
        cmd.SetAction(async (ctx, token) =>
        {
            var dryRun = ctx.GetValue(dryRunOpt);
            var repoRoot = ctx.GetValue(repoRootOpt);
            return await updater.UpdateRunnerAsync(repoRoot, dryRun, token);
        });
        return cmd;
    }
}

internal enum UpdateStage
{
    Start,
    UpdateCli,
    PrepareRunner,
    UpdateServer,
    WaitingForReady,
    RestoreRunner,
    VerifyRuntime,
    Complete,
}

internal enum RuntimeCheckOutcome
{
    Pass,
    Warn,
    Fail,
}

internal sealed record RuntimeCheckResult(string Component, RuntimeCheckOutcome Outcome, string Message);

internal sealed record UpdateStageLogEntry(string Stage, string Message, DateTimeOffset At);

internal enum UpdateOutcome
{
    Ready,
    Recovered,
    Failed,
}

internal sealed record CliOutcomeLogEntry(DateTimeOffset At, string Stage, string Message);

internal sealed record CliOutcomeRequest(
    string? JobId,
    string? Status,
    string? Stage,
    string? Outcome,
    string? UnavailableCapability,
    IReadOnlyList<CliOutcomeLogEntry>? Logs,
    string? SourceHead);

internal static class CliOutcomeJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

internal sealed class UpdateContext
{
    public UpdateContext(bool dryRun, string? repoRoot, string? cliPath, CancellationToken cancellationToken)
    {
        DryRun = dryRun;
        RepoRoot = repoRoot;
        CliPath = cliPath;
        CancellationToken = cancellationToken;
        JobId = Guid.NewGuid().ToString("N");
    }

    public bool DryRun { get; }
    public string? RepoRoot { get; }
    public string? CliPath { get; }
    public CancellationToken CancellationToken { get; }

    public string JobId { get; }
    public UpdateStage Stage { get; set; } = UpdateStage.Start;
    public bool RunnerWasRunning { get; set; }
    public bool RunnerInstalled { get; set; }
    public bool RunnerStopped { get; set; }
    public bool RunnerRestored { get; set; }
    public bool Interrupted { get; set; }
    public List<string> Warnings { get; } = new();
    public List<UpdateStageLogEntry> StageLogEntries { get; } = new();
    public List<RuntimeCheckResult> RuntimeChecks { get; } = new();
    public UpdateOutcome? Outcome { get; set; }
    public string? UnavailableCapability { get; set; }
    public string? SourceHead { get; set; }
    public int LastExitCode { get; set; }

    public void RecordWarning(string warning)
    {
        Warnings.Add(warning);
    }

    public void RecordStage(string label, string message)
    {
        StageLogEntries.Add(new UpdateStageLogEntry(label, message, DateTimeOffset.UtcNow));
    }

    public void RecordRuntimeCheck(RuntimeCheckResult check)
    {
        RuntimeChecks.Add(check);
        if (check.Outcome == RuntimeCheckOutcome.Warn)
        {
            Warnings.Add($"{check.Component}: {check.Message}");
        }
    }
}

internal sealed class SourceCodeUpdater
{
    private static readonly TimeSpan ServerReadyTimeout = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan ServerReadyPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ServerReadyProgressInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RunnerIdentityTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RunnerIdentityPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly string RunnerDistBuildInfoRelativePath = Path.Combine("packages", "runner", "dist", "build-info.json");

    private readonly TextWriter _out;
    private readonly TextWriter _err;
    private readonly IServiceInstaller _systemd;
    private readonly ICommandExecutor _commandExecutor;
    private readonly IFileSystem _fileSystem;
    private readonly IEnvironmentVariableProvider _environment;
    private readonly HttpClient _http;
    private readonly TimeSpan _serverReadyTimeout;
    private readonly TimeSpan _runnerIdentityTimeout;
    private readonly Func<string?> _getUserHome;
    private readonly Func<string?> _getLocalHostname;
    private readonly string? _unitDir;

    public SourceCodeUpdater(
        TextWriter output,
        TextWriter error,
        IServiceInstaller systemd,
        ICommandExecutor commandExecutor,
        IFileSystem? fileSystem = null,
        IEnvironmentVariableProvider? environment = null,
        HttpClient? http = null,
        TimeSpan? serverReadyTimeout = null,
        Func<string?>? getUserHome = null,
        TimeSpan? runnerIdentityTimeout = null,
        Func<string?>? getLocalHostname = null,
        string? unitDir = null)
    {
        _out = output;
        _err = error;
        _systemd = systemd;
        _commandExecutor = commandExecutor;
        _fileSystem = fileSystem ?? RealFileSystem.Instance;
        _environment = environment ?? SystemEnvironmentVariableProvider.Instance;
        _http = http ?? new HttpClient
        {
            BaseAddress = new Uri(_environment.GetEnvironmentVariable(ServerUrlEnvironmentVariable) ?? "http://127.0.0.1:3456"),
            Timeout = TimeSpan.FromSeconds(5),
        };
        _serverReadyTimeout = serverReadyTimeout ?? ServerReadyTimeout;
        _runnerIdentityTimeout = runnerIdentityTimeout ?? RunnerIdentityTimeout;
        _getUserHome = getUserHome ?? DefaultUserHome;
        _getLocalHostname = getLocalHostname ?? DefaultLocalHostname;
        _unitDir = unitDir;
    }

    public const string ServerUrlEnvironmentVariable = "MOHIST_SERVER_URL";

    private string? DefaultUserHome()
    {
        var home = _environment.GetEnvironmentVariable(SkillAssetRootResolver.HomeEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(home))
            return home;
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private static string? DefaultLocalHostname() => Environment.MachineName;

    internal string ResolveManagedSkillAssetRoot()
    {
        var home = _getUserHome();
        if (string.IsNullOrWhiteSpace(home))
            return Path.Combine(AppContext.BaseDirectory, "skill-data");
        return Path.Combine(home, ".mohist", "cli", "skill-data");
    }

    internal Uri? ServerBaseAddress => _http.BaseAddress;

    public async Task<int> UpdateAllAsync(string? repoRoot, bool dryRun, string? cliPath = null, CancellationToken cancellationToken = default)
    {
        var context = new UpdateContext(dryRun, repoRoot, cliPath, cancellationToken);
        var outcome = await RunStageMachineAsync(context, async (ctx, token) =>
        {
            return await UpdateCliStageAsync(ctx, token);
        });

        if (context.Interrupted)
        {
            if (!context.RunnerStopped)
            {
                _out.WriteLine("Update cancelled before the runner was stopped. No recovery needed.");
            }
            return await FinalizeAsync(context, 130);
        }

        if (!outcome.Success)
        {
            return await FinalizeAsync(context, outcome.ExitCode);
        }

        outcome = await RunStageMachineAsync(context, async (ctx, token) =>
        {
            return await PrepareRunnerStageAsync(ctx, token);
        });

        if (context.Interrupted || !outcome.Success)
        {
            return await FinalizeAfterServerAsync(context, outcome);
        }

        outcome = await RunStageMachineAsync(context, async (ctx, token) =>
        {
            return await UpdateServerStageAsync(ctx, token);
        });

        if (context.Interrupted || !outcome.Success)
        {
            return await FinalizeAfterServerAsync(context, outcome);
        }

        outcome = await RunStageMachineAsync(context, async (ctx, token) =>
        {
            return await WaitingForReadyStageAsync(ctx, token);
        });

        if (context.Interrupted || !outcome.Success)
        {
            return await FinalizeAfterServerAsync(context, outcome);
        }

        outcome = await RunStageMachineAsync(context, async (ctx, token) =>
        {
            return await RestoreRunnerStageAsync(ctx, token);
        });

        if (context.Interrupted || !outcome.Success)
        {
            return await FinalizeAsync(context, outcome.ExitCode);
        }

        outcome = await RunStageMachineAsync(context, async (ctx, token) =>
        {
            return await VerifyRuntimeStageAsync(ctx, token);
        });

        return await FinalizeAsync(context, outcome.Success ? 0 : outcome.ExitCode);
    }

    private async Task<int> FinalizeAfterServerAsync(UpdateContext context, StageOutcome outcome)
    {
        if (context.RunnerWasRunning && !context.RunnerRestored)
        {
            if (context.Interrupted)
                _err.WriteLine("Update was interrupted and the runner was stopped. Attempting runner restore.");
            else
                _err.WriteLine("Update failed after the runner was stopped. Attempting runner restore.");

            var restore = await RunRecoveryStageAsync(context, async (ctx, token) =>
            {
                return await RestoreRunnerStageAsync(ctx, token);
            });

            if (!restore.Success && restore.ExitCode != 0 && context.LastExitCode == 0)
            {
                context.LastExitCode = restore.ExitCode;
            }
        }

        return await FinalizeAsync(context, outcome.ExitCode);
    }

    private async Task<StageOutcome> RunStageMachineAsync(UpdateContext context, Func<UpdateContext, CancellationToken, Task<int>> stage)
    {
        if (context.CancellationToken.IsCancellationRequested)
        {
            context.Interrupted = true;
            return new StageOutcome(false, 130, new OperationCanceledException(context.CancellationToken));
        }

        try
        {
            var exitCode = await stage(context, context.CancellationToken);
            context.LastExitCode = exitCode;
            if (context.CancellationToken.IsCancellationRequested)
            {
                context.Interrupted = true;
                return new StageOutcome(false, 130, new OperationCanceledException(context.CancellationToken));
            }
            return new StageOutcome(exitCode == 0, exitCode, null);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            context.Interrupted = true;
            return new StageOutcome(false, 130, null);
        }
        catch (Exception ex)
        {
            _err.WriteLine($"Update stage failed: {ex.GetType().Name}: {ex.Message}");
            return new StageOutcome(false, 1, ex);
        }
    }

    private async Task<StageOutcome> RunRecoveryStageAsync(UpdateContext context, Func<UpdateContext, CancellationToken, Task<int>> stage)
    {
        using var recoveryCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        try
        {
            var exitCode = await stage(context, recoveryCts.Token);
            return new StageOutcome(exitCode == 0, exitCode, null);
        }
        catch (OperationCanceledException) when (recoveryCts.IsCancellationRequested)
        {
            return new StageOutcome(false, 1, new OperationCanceledException(recoveryCts.Token));
        }
        catch (Exception ex)
        {
            _err.WriteLine($"Update recovery stage failed: {ex.GetType().Name}: {ex.Message}");
            return new StageOutcome(false, 1, ex);
        }
    }

    private async Task<int> UpdateCliStageAsync(UpdateContext context, CancellationToken token)
    {
        context.Stage = UpdateStage.UpdateCli;
        _out.WriteLine(StageLabels.CliUpdate);
        context.RecordStage(StageLabels.CliUpdate, "starting");

        var exitCode = await UpdateCliAsync(context.RepoRoot, context.DryRun, context.CliPath, token);
        if (exitCode != 0)
        {
            context.RecordStage(StageLabels.CliUpdate, "failed");
            return exitCode;
        }
        context.RecordStage(StageLabels.CliUpdate, "complete");
        return 0;
    }

    private async Task<int> PrepareRunnerStageAsync(UpdateContext context, CancellationToken token)
    {
        context.Stage = UpdateStage.PrepareRunner;
        _out.WriteLine(StageLabels.PrepareRunner);
        context.RecordStage(StageLabels.PrepareRunner, "querying runner state");

        context.RunnerInstalled = context.DryRun || await _systemd.IsRunnerInstalledAsync(_unitDir);
        if (!context.RunnerInstalled)
        {
            var reason = "runner service is not installed";
            context.RecordStage(StageLabels.PrepareRunner, reason);
            _out.WriteLine("Runner service is not installed; skipping pre-server runner stop.");
            _out.WriteLine($"Runner refresh skipped: {reason}");
            WriteRunnerRefreshSummary(new RunnerRefreshOutcome.Skipped(reason));
            return 0;
        }

        if (!context.DryRun)
        {
            context.RunnerWasRunning = await _systemd.IsRunnerRunningAsync(token);
        }
        else
        {
            context.RunnerWasRunning = true;
            _out.WriteLine("Dry run: would query systemctl --user is-active mohist-runner.service");
        }

        if (!context.RunnerWasRunning)
        {
            context.RecordStage(StageLabels.PrepareRunner, "runner not running; nothing to stop");
            _out.WriteLine("Runner was not running; nothing to stop for the server update.");
            return 0;
        }

        context.RecordStage(StageLabels.PrepareRunner, "stopping runner for server update");
        var stop = await _systemd.StopRunnerAsync(new ServiceCommandOptions(context.DryRun, null, 100, false));
        if (stop != 0)
        {
            context.RecordStage(StageLabels.PrepareRunner, "stop failed");
            return stop;
        }

        context.RunnerStopped = true;
        _out.WriteLine("Runner is stopped. Workflows cannot run until the runner is restored.");
        context.RecordStage(StageLabels.PrepareRunner, "runner stopped; workflows paused");
        return 0;
    }

    private async Task<int> UpdateServerStageAsync(UpdateContext context, CancellationToken token)
    {
        context.Stage = UpdateStage.UpdateServer;
        _out.WriteLine(StageLabels.UpdateServer);
        context.RecordStage(StageLabels.UpdateServer, "building and restarting server");

        if (context.DryRun)
        {
            _out.WriteLine($"  cd {ResolveRepoRoot(context.RepoRoot)} && dotnet build Mohist.sln");
            _out.WriteLine($"  {RestartCommandLine("server")} (if installed)");
            context.RecordStage(StageLabels.UpdateServer, "complete (dry run)");
            return 0;
        }

        var root = ResolveRepoRoot(context.RepoRoot);
        var exitCode = await BuildAndRestartServerAsync(root, token);
        if (exitCode != 0)
        {
            context.RecordStage(StageLabels.UpdateServer, "failed");
            return exitCode;
        }
        context.RecordStage(StageLabels.UpdateServer, "complete");
        return 0;
    }

    private async Task<int> WaitingForReadyStageAsync(UpdateContext context, CancellationToken token)
    {
        context.Stage = UpdateStage.WaitingForReady;
        _out.WriteLine(StageLabels.WaitingForReady);
        context.RecordStage(StageLabels.WaitingForReady, "starting readiness checks");

        if (context.DryRun)
        {
            _out.WriteLine("Dry run: would wait for /api/health, /, and referenced /assets/* readiness checks.");
            return 0;
        }

        var ready = await WaitForServerReadyWithProgressAsync(_serverReadyTimeout, token);
        if (!ready.Ready)
        {
            context.RecordStage(StageLabels.WaitingForReady, $"timed out: {ready.LastFailure ?? "no readiness signal"}");
            return 1;
        }
        context.RecordStage(StageLabels.WaitingForReady, "server is ready");
        return 0;
    }

    private async Task<int> RestoreRunnerStageAsync(UpdateContext context, CancellationToken token)
    {
        context.Stage = UpdateStage.RestoreRunner;
        _out.WriteLine(StageLabels.RestoreRunner);
        context.RecordStage(StageLabels.RestoreRunner, "starting runner restore");

        if (!context.RunnerWasRunning)
        {
            _out.WriteLine("Runner was not running before the update; no restore needed.");
            context.RecordStage(StageLabels.RestoreRunner, "skipped; runner was not running");
            return 0;
        }

        if (!context.DryRun)
        {
            var root = ResolveRepoRoot(context.RepoRoot);
            var (build, buildOut, buildErr) = await _commandExecutor.ExecuteAsync("npm", ["run", "build", "-w", "packages/runner"], root);
            if (build != 0)
            {
                WriteCommandFailureOutput(buildOut, buildErr);
                context.RecordStage(StageLabels.RestoreRunner, "runner build failed");
                context.UnavailableCapability ??= "Runner unavailable";
                return build;
            }
            _out.WriteLine("Runner updated successfully.");
        }
        else
        {
            var root = ResolveRepoRoot(context.RepoRoot);
            _out.WriteLine($"  cd {root} && npm run build -w packages/runner");
        }

        var start = await _systemd.StartRunnerAsync(new ServiceCommandOptions(context.DryRun, null, 100, false));
        if (start != 0)
        {
            context.RecordStage(StageLabels.RestoreRunner, "failed to start");
            context.UnavailableCapability ??= "Runner unavailable";
            return start;
        }

        context.RunnerRestored = true;
        _out.WriteLine("Runner service restored.");
        context.RecordStage(StageLabels.RestoreRunner, "runner started");
        return 0;
    }

    private async Task<int> VerifyRuntimeStageAsync(UpdateContext context, CancellationToken token)
    {
        context.Stage = UpdateStage.VerifyRuntime;
        _out.WriteLine(StageLabels.VerifyRuntime);
        context.RecordStage(StageLabels.VerifyRuntime, "starting runtime consistency checks");

        if (context.DryRun)
        {
            _out.WriteLine("Dry run: would verify CLI binary, server identity, web assets, runner connection, and managed skill assets.");
            context.Outcome = UpdateOutcome.Ready;
            context.RecordStage(StageLabels.VerifyRuntime, "skipped (dry run)");
            return 0;
        }

        var checks = new List<RuntimeCheckResult>
        {
            await CheckCliBinaryAsync(context, token),
            await CheckServerIdentityAsync(context, token),
            await CheckWebAssetsAsync(context, token),
            await CheckRunnerConnectionAsync(context, token),
            await CheckManagedSkillAssetsAsync(context, token),
        };

        foreach (var check in checks)
        {
            context.RecordRuntimeCheck(check);
            switch (check.Outcome)
            {
                case RuntimeCheckOutcome.Pass:
                    _out.WriteLine($"  [ok] {check.Component}: {check.Message}");
                    break;
                case RuntimeCheckOutcome.Warn:
                    _out.WriteLine($"  [warn] {check.Component}: {check.Message}");
                    break;
                case RuntimeCheckOutcome.Fail:
                    _err.WriteLine($"  [fail] {check.Component}: {check.Message}");
                    break;
            }
        }

        if (checks.Any(c => c.Outcome == RuntimeCheckOutcome.Fail))
        {
            var firstFailure = checks.First(c => c.Outcome == RuntimeCheckOutcome.Fail);
            var capability = string.Equals(firstFailure.Component, "Runner connection", StringComparison.Ordinal)
                ? "Runner unavailable"
                : firstFailure.Component;
            context.UnavailableCapability ??= capability;
            context.Outcome = UpdateOutcome.Failed;
            context.RecordStage(StageLabels.VerifyRuntime, $"failed: {capability}");
            return 1;
        }

        if (checks.Any(c => c.Outcome == RuntimeCheckOutcome.Warn))
        {
            context.Outcome = UpdateOutcome.Recovered;
            context.RecordStage(StageLabels.VerifyRuntime, "recovered with warnings");
            return 0;
        }

        context.Outcome = UpdateOutcome.Ready;
        context.RecordStage(StageLabels.VerifyRuntime, "all checks passed");
        return 0;
    }

    internal async Task<RuntimeCheckResult> CheckCliBinaryAsync(UpdateContext context, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(context.CliPath))
        {
            return new RuntimeCheckResult("CLI binary", RuntimeCheckOutcome.Fail,
                "CLI binary path was not resolved; cannot invoke mo --version. Reinstall with 'mo update' or pass --cli-path.");
        }

        try
        {
            var (exitCode, stdout, stderr) = await _commandExecutor.ExecuteAsync(context.CliPath, ["--version"], null);
            if (exitCode != 0)
            {
                return new RuntimeCheckResult("CLI binary", RuntimeCheckOutcome.Fail,
                    $"mo --version exited with code {exitCode}: {stderr.Trim()}");
            }

            var versionOutput = stdout.Trim();
            if (string.IsNullOrWhiteSpace(versionOutput))
            {
                return new RuntimeCheckResult("CLI binary", RuntimeCheckOutcome.Warn,
                    "mo --version reported an empty version string.");
            }

            return new RuntimeCheckResult("CLI binary", RuntimeCheckOutcome.Pass,
                $"mo --version reported '{versionOutput}'");
        }
        catch (Exception ex)
        {
            return new RuntimeCheckResult("CLI binary", RuntimeCheckOutcome.Fail,
                $"mo --version failed: {ex.Message}");
        }
    }

    internal async Task<RuntimeCheckResult> CheckServerIdentityAsync(UpdateContext context, CancellationToken token)
    {
        var info = await TryGetSystemInfoAsync(token);
        if (info is null)
        {
            return new RuntimeCheckResult("Server identity", RuntimeCheckOutcome.Fail,
                "GET /api/system/info did not respond");
        }

        var runningHash = info.Running?.GitHash;
        if (string.IsNullOrWhiteSpace(runningHash))
        {
            return new RuntimeCheckResult("Server identity", RuntimeCheckOutcome.Warn,
                "Server reported an empty git hash; cannot verify identity");
        }

        var sourceHead = await TryGetSourceHeadAsync(context);
        if (string.IsNullOrWhiteSpace(sourceHead))
        {
            return new RuntimeCheckResult("Server identity", RuntimeCheckOutcome.Warn,
                "Source HEAD could not be determined; skipping identity check");
        }

        if (!string.Equals(runningHash, sourceHead, StringComparison.Ordinal))
        {
            return new RuntimeCheckResult("Server identity", RuntimeCheckOutcome.Warn,
                $"Running server git hash '{runningHash}' does not match source HEAD '{sourceHead}'");
        }

        return new RuntimeCheckResult("Server identity", RuntimeCheckOutcome.Pass,
            $"Server identity matches source HEAD '{sourceHead}'");
    }

    internal async Task<RuntimeCheckResult> CheckWebAssetsAsync(UpdateContext context, CancellationToken token)
    {
        try
        {
            using var index = await _http.GetAsync("/", HttpCompletionOption.ResponseHeadersRead, token);
            if (!index.IsSuccessStatusCode)
            {
                return new RuntimeCheckResult("Web assets", RuntimeCheckOutcome.Fail,
                    $"GET / returned {(int)index.StatusCode} {index.StatusCode}");
            }

            var contentType = index.Content.Headers.ContentType?.MediaType;
            if (!string.Equals(contentType, "text/html", StringComparison.OrdinalIgnoreCase))
            {
                return new RuntimeCheckResult("Web assets", RuntimeCheckOutcome.Fail,
                    $"GET / returned content type '{contentType ?? "unknown"}', expected text/html");
            }

            var html = await index.Content.ReadAsStringAsync(token);
            var assetPath = FindFirstAssetPath(html);
            if (assetPath is null)
            {
                return new RuntimeCheckResult("Web assets", RuntimeCheckOutcome.Fail,
                    "GET / did not reference a /assets/* bundle");
            }

            using var asset = await _http.GetAsync(assetPath, HttpCompletionOption.ResponseHeadersRead, token);
            if (asset.StatusCode != HttpStatusCode.OK)
            {
                return new RuntimeCheckResult("Web assets", RuntimeCheckOutcome.Fail,
                    $"GET {assetPath} returned {(int)asset.StatusCode} {asset.StatusCode}");
            }

            return new RuntimeCheckResult("Web assets", RuntimeCheckOutcome.Pass,
                $"Web root and {assetPath} respond with expected content");
        }
        catch (Exception ex)
        {
            return new RuntimeCheckResult("Web assets", RuntimeCheckOutcome.Fail,
                $"Web asset check failed: {ex.Message}");
        }
    }

    internal async Task<RuntimeCheckResult> CheckRunnerConnectionAsync(UpdateContext context, CancellationToken token)
    {
        var info = await TryGetSystemInfoAsync(token);
        if (info is null)
        {
            return new RuntimeCheckResult("Runner connection", RuntimeCheckOutcome.Fail,
                "GET /api/system/info did not respond");
        }

        var runner = info.Services?.Runner;
        if (string.IsNullOrWhiteSpace(runner))
        {
            return new RuntimeCheckResult("Runner connection", RuntimeCheckOutcome.Fail,
                "Server did not report a runner service state");
        }

        if (string.Equals(runner, "active", StringComparison.OrdinalIgnoreCase))
        {
            return new RuntimeCheckResult("Runner connection", RuntimeCheckOutcome.Pass,
                "Runner service is active");
        }

        return new RuntimeCheckResult("Runner connection", RuntimeCheckOutcome.Fail,
            $"Runner service is '{runner}'; expected 'active'");
    }

    internal async Task<RuntimeCheckResult> CheckManagedSkillAssetsAsync(UpdateContext context, CancellationToken token)
    {
        await Task.CompletedTask;
        var assetRoot = ResolveManagedSkillAssetRoot();
        if (!_fileSystem.DirectoryExists(assetRoot))
        {
            return new RuntimeCheckResult("Managed skill assets", RuntimeCheckOutcome.Warn,
                $"Managed skill assets are missing at '{assetRoot}'. Run 'mo skills install' to restore.");
        }

        try
        {
            var hasSkill = _fileSystem
                .EnumerateFiles(assetRoot, "SKILL.md", SearchOption.AllDirectories)
                .Any();

            if (!hasSkill)
            {
                return new RuntimeCheckResult("Managed skill assets", RuntimeCheckOutcome.Warn,
                    $"Managed skill assets at '{assetRoot}' contain no skill. Run 'mo skills install' to restore.");
            }
        }
        catch (Exception ex)
        {
            return new RuntimeCheckResult("Managed skill assets", RuntimeCheckOutcome.Warn,
                $"Failed to inspect managed skill assets at '{assetRoot}': {ex.Message}");
        }

        return new RuntimeCheckResult("Managed skill assets", RuntimeCheckOutcome.Pass,
            $"Skill assets present at '{assetRoot}'");
    }

    private async Task<SystemInfoSnapshot?> TryGetSystemInfoAsync(CancellationToken token)
    {
        try
        {
            using var response = await _http.GetAsync("/api/system/info", HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode)
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync(token);
            return await JsonSerializer.DeserializeAsync<SystemInfoSnapshot>(stream, SystemInfoSnapshot.JsonOptions, token);
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> TryGetSourceHeadAsync(UpdateContext context)
    {
        if (!string.IsNullOrWhiteSpace(context.SourceHead))
            return context.SourceHead;

        try
        {
            var root = ResolveRepoRoot(context.RepoRoot);
            var (exitCode, stdout, _) = await _commandExecutor.ExecuteAsync("git", ["rev-parse", "HEAD"], root);
            if (exitCode != 0)
                return null;
            var head = stdout.Trim();
            if (string.IsNullOrWhiteSpace(head))
                return null;
            context.SourceHead = head;
            return head;
        }
        catch
        {
            return null;
        }
    }

    private async Task<int> FinalizeAsync(UpdateContext context, int exitCode)
    {
        context.Stage = UpdateStage.Complete;

        if (context.RunnerWasRunning && !context.RunnerRestored)
        {
            if (context.Interrupted)
                _err.WriteLine("Update was interrupted and the runner was stopped. Runner restore was attempted.");
            else
                _err.WriteLine("Update failed after the runner was stopped. Runner restore was attempted.");
        }

        await Task.CompletedTask;

        var finalExit = FinalizeExitCode(context, exitCode);

        if (ShouldPostOutcome(context))
        {
            await PostCliOutcomeAsync(context, context.CancellationToken);
        }
        else if (context.Interrupted)
        {
            _out.WriteLine("Update was cancelled. The local terminal output above is the authoritative result; no outcome was posted to the server.");
        }

        return finalExit;
    }

    private static bool ShouldPostOutcome(UpdateContext context)
    {
        if (context.DryRun)
            return false;

        if (!string.IsNullOrEmpty(context.UnavailableCapability))
            return true;

        if (context.Interrupted)
            return false;

        return true;
    }

    private int FinalizeExitCode(UpdateContext context, int? overrideExitCode = null)
    {
        var exit = overrideExitCode ?? context.LastExitCode;

        if (context.RunnerWasRunning && !context.RunnerRestored)
        {
            context.UnavailableCapability ??= "Runner unavailable";
        }

        if (context.Interrupted)
        {
            _out.WriteLine("Update was interrupted.");
        }

        if (!string.IsNullOrEmpty(context.UnavailableCapability))
        {
            _err.WriteLine($"Mohist is not fully usable. Unavailable capability: {context.UnavailableCapability}.");
            if (string.Equals(context.UnavailableCapability, "Runner unavailable", StringComparison.Ordinal))
            {
                _err.WriteLine("Start the runner manually with: mo server start --runner");
            }
        }
        else if (context.Warnings.Count > 0)
        {
            _out.WriteLine("Mohist is recovered with warnings.");
            foreach (var warning in context.Warnings)
                _out.WriteLine($"  - {warning}");
        }
        else if (exit == 0)
        {
            _out.WriteLine("Update complete. Mohist is ready.");
        }
        else
        {
            _err.WriteLine("Mohist update did not complete successfully.");
        }

        return exit == 0 && context.Interrupted ? 130 : exit;
    }

    internal async Task<bool> PostCliOutcomeAsync(UpdateContext context, CancellationToken token)
    {
        if (context.DryRun)
        {
            _out.WriteLine("Dry run: would POST update outcome to server.");
            return false;
        }

        if (context.StageLogEntries.Count == 0)
        {
            // Nothing to report.
            return false;
        }

        var (status, outcomeLabel) = ResolveOutcomeStatus(context);
        var stage = context.StageLogEntries[^1].Stage;
        var unavailableCapability = !string.IsNullOrEmpty(context.UnavailableCapability)
            ? context.UnavailableCapability
            : null;

        var logs = context.StageLogEntries
            .Select(e => new CliOutcomeLogEntry(e.At, e.Stage, e.Message))
            .ToList();

        var payload = new CliOutcomeRequest(
            JobId: context.JobId,
            Status: status,
            Stage: stage,
            Outcome: outcomeLabel,
            UnavailableCapability: unavailableCapability,
            Logs: logs,
            SourceHead: context.SourceHead);

        using var postCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        postCts.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/system/update/outcome")
            {
                Content = JsonContent.Create(payload, options: CliOutcomeJson.Options),
            };
            using var response = await _http.SendAsync(request, postCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                _out.WriteLine($"Could not persist update outcome to server (HTTP {(int)response.StatusCode}). The CLI terminal output above is the authoritative result.");
                return false;
            }

            _out.WriteLine("Update outcome persisted to server.");
            return true;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            _out.WriteLine("Could not persist update outcome to server (timed out). The CLI terminal output above is the authoritative result.");
            return false;
        }
        catch (Exception ex)
        {
            _out.WriteLine($"Could not persist update outcome to server: {ex.GetType().Name}: {ex.Message}. The CLI terminal output above is the authoritative result.");
            return false;
        }
    }

    private static (string Status, string Outcome) ResolveOutcomeStatus(UpdateContext context)
    {
        if (!string.IsNullOrEmpty(context.UnavailableCapability))
            return ("failed", "failed");

        if (context.Interrupted)
            return ("cancelled", "failed");

        return context.Outcome switch
        {
            UpdateOutcome.Recovered => ("recovered", "recovered"),
            UpdateOutcome.Failed => ("failed", "failed"),
            UpdateOutcome.Ready when context.LastExitCode != 0 => ("failed", "failed"),
            UpdateOutcome.Ready => ("succeeded", "succeeded"),
            _ when context.LastExitCode != 0 => ("failed", "failed"),
            _ => ("succeeded", "succeeded"),
        };
    }

    public async Task<int> UpdateCliAsync(string? repoRoot, bool dryRun, string? cliPath = null, CancellationToken cancellationToken = default)
    {
        var root = ResolveRepoRoot(repoRoot);
        var target = await ResolveCliPathAsync(cliPath);
        if (string.IsNullOrWhiteSpace(target))
        {
            _err.WriteLine("Could not resolve mo executable path. Pass --cli-path to update the CLI explicitly.");
            return 1;
        }

        var publishDir = Path.Combine(root, ".publish", "cli");
        var binary = Path.Combine(publishDir, "Mohist.Cli");
        var tempTarget = $"{target}.tmp";
        var sourceSkillData = Path.Combine(publishDir, "skill-data");
        var managedSkillData = ResolveManagedSkillAssetRoot();

        _out.WriteLine($"Updating CLI from source: {root}");

        if (dryRun)
        {
            _out.WriteLine("Dry run: would execute:");
            _out.WriteLine($"  cd {root} && dotnet publish packages/cli/Mohist.Cli/Mohist.Cli.csproj -c Release -r {RuntimeIdentifier()} --self-contained true /p:PublishSingleFile=true -o {publishDir}");
            _out.WriteLine($"  cp {binary} {tempTarget}");
            _out.WriteLine($"  chmod +x {tempTarget}");
            _out.WriteLine($"  mv {tempTarget} {target}");
            _out.WriteLine($"  synchronize {sourceSkillData} into {managedSkillData} (prepare temp dir, replace managed root)");
            return 0;
        }

        var publishArgs = new[]
        {
            "publish",
            "packages/cli/Mohist.Cli/Mohist.Cli.csproj",
            "-c",
            "Release",
            "-r",
            RuntimeIdentifier(),
            "--self-contained",
            "true",
            "/p:PublishSingleFile=true",
            "-o",
            publishDir,
        };
        var (publish, publishOut, publishErr) = await _commandExecutor.ExecuteAsync("dotnet", publishArgs, root);
        if (publish != 0)
        {
            WriteCommandFailureOutput(publishOut, publishErr);
            _err.WriteLine("CLI publish failed. Aborting update.");
            return publish;
        }

        var (copy, _, copyErr) = await _commandExecutor.ExecuteAsync("cp", [binary, tempTarget], root);
        if (copy != 0)
        {
            if (!string.IsNullOrWhiteSpace(copyErr)) _err.WriteLine(copyErr);
            _err.WriteLine("CLI install failed. Aborting update.");
            return copy;
        }

        var (chmod, _, chmodErr) = await _commandExecutor.ExecuteAsync("chmod", ["+x", tempTarget], root);
        if (chmod != 0)
        {
            if (!string.IsNullOrWhiteSpace(chmodErr)) _err.WriteLine(chmodErr);
            _err.WriteLine("CLI chmod failed. Aborting update.");
            return chmod;
        }

        var (move, _, moveErr) = await _commandExecutor.ExecuteAsync("mv", [tempTarget, target], root);
        if (move != 0)
        {
            if (!string.IsNullOrWhiteSpace(moveErr)) _err.WriteLine(moveErr);
            _err.WriteLine("CLI replace failed. Aborting update.");
            return move;
        }

        var synchronizer = new SkillAssetSynchronizer(_out, _err, _fileSystem);
        var syncExitCode = await synchronizer.SyncAsync(sourceSkillData, managedSkillData);
        if (syncExitCode != 0)
        {
            _err.WriteLine("Managed skill asset sync failed. Aborting update.");
            return syncExitCode;
        }

        _out.WriteLine($"CLI updated: {target}");
        return 0;
    }

    public async Task<int> UpdateServerAsync(string? repoRoot, bool dryRun, CancellationToken cancellationToken = default)
    {
        var root = ResolveRepoRoot(repoRoot);

        _out.WriteLine($"Updating server from source: {root}");

        if (dryRun)
        {
            _out.WriteLine("Dry run: would execute:");
            _out.WriteLine($"  cd {root} && dotnet build Mohist.sln");
            _out.WriteLine($"  {RestartCommandLine("server")} (if installed)");
            _out.WriteLine("  wait for /api/health, /, and referenced /assets/* response headers readiness checks");
            await WriteServerScopeMessageAsync();
            return 0;
        }

        var exitCode = await BuildAndRestartServerAsync(root, cancellationToken);
        if (exitCode != 0)
            return exitCode;

        _out.WriteLine("Server service restarted.");
        var ready = await WaitForServerReadyAsync(_serverReadyTimeout, cancellationToken);
        if (!ready.Ready)
        {
            _err.WriteLine($"Server service restarted, but Mohist readiness checks did not pass within {(int)_serverReadyTimeout.TotalSeconds} seconds.");
            if (!string.IsNullOrWhiteSpace(ready.LastFailure))
                _err.WriteLine($"Last readiness error: {ready.LastFailure}");
            return 1;
        }

        _out.WriteLine("Server is ready.");
        await WriteServerScopeMessageAsync();
        return 0;
    }

    private async Task<int> BuildAndRestartServerAsync(string root, CancellationToken cancellationToken)
    {
        var (build, buildOut, buildErr) = await _commandExecutor.ExecuteAsync("dotnet", ["build", "Mohist.sln"], root);
        if (build != 0)
        {
            WriteCommandFailureOutput(buildOut, buildErr);
            _err.WriteLine("Build failed. Aborting update.");
            return build;
        }

        _out.WriteLine("Server updated successfully.");

        var restart = await _systemd.RestartServerAsync(new ServiceCommandOptions(false, null, 100, false));
        if (restart != 0)
        {
            _err.WriteLine("Warning: Failed to restart server service. You may need to restart manually.");
            return restart;
        }

        return 0;
    }

    /// <summary>
    /// Emits the explicit "server-only" scope message: the runner was not refreshed, and,
    /// when the runner is installed locally, a concrete follow-up command to refresh it.
    /// Messaging-only; no behavioral change to <see cref="UpdateServerAsync"/>.
    /// </summary>
    private async Task WriteServerScopeMessageAsync()
    {
        _out.WriteLine("Note: 'mo update server' did not refresh the runner build output or runner runtime.");
        _out.WriteLine("Local runner code may now be stale relative to the updated server.");
        var installed = await _systemd.IsRunnerInstalledAsync(_unitDir);
        if (installed)
        {
            _out.WriteLine("To refresh the runner, run: mo update runner");
            _out.WriteLine("Or, to refresh CLI + server + runner together, run: mo update");
        }
        else
        {
            _out.WriteLine("No runner service is installed locally; runner refresh is not required.");
        }
    }

    public async Task<int> UpdateRunnerAsync(string? repoRoot, bool dryRun, CancellationToken cancellationToken = default)
    {
        var root = ResolveRepoRoot(repoRoot);

        _out.WriteLine($"Updating runner from source: {root}");

        if (dryRun)
        {
            _out.WriteLine("Dry run: would execute:");
            _out.WriteLine($"  cd {root} && npm run build -w packages/runner");
            _out.WriteLine($"  {RestartCommandLine("runner")} (if installed)");
            _out.WriteLine("  wait for runner to reconnect, then read its buildGitHash from /api/runner/identity");
            return 0;
        }

        var installed = await _systemd.IsRunnerInstalledAsync(_unitDir);
        if (!installed)
        {
            var reason = "runner service is not installed";
            _out.WriteLine($"Runner refresh skipped: {reason}");
            WriteRunnerRefreshSummary(new RunnerRefreshOutcome.Skipped(reason));
            return 0;
        }

        var (build, buildOut, buildErr) = await _commandExecutor.ExecuteAsync("npm", ["run", "build", "-w", "packages/runner"], root);
        if (build != 0)
        {
            WriteCommandFailureOutput(buildOut, buildErr);
            _err.WriteLine("Build failed. Aborting update.");
            return build;
        }

        _out.WriteLine("Runner updated successfully.");

        var restart = await _systemd.RestartRunnerAsync(new ServiceCommandOptions(false, null, 100, false));
        if (restart != 0)
        {
            _err.WriteLine("Warning: Failed to restart runner service. You may need to restart manually.");
            return restart;
        }

        _out.WriteLine("Runner service restarted.");

        var outcome = await VerifyRunnerRuntimeAsync(root);
        WriteRunnerRefreshSummary(outcome);
        return outcome.ExitCode;
    }

    private void WriteRunnerRefreshSummary(RunnerRefreshOutcome outcome)
    {
        switch (outcome)
        {
            case RunnerRefreshOutcome.Current:
                _out.WriteLine("Runner runtime verification: current (matches repo HEAD).");
                break;
            case RunnerRefreshOutcome.UnknownIdentity unknown:
                _out.WriteLine($"Runner runtime verification: unknown-identity ({unknown.Reason}).");
                break;
            case RunnerRefreshOutcome.NotReconnected:
                _err.WriteLine("Runner runtime verification: runner-not-reconnected (runner did not report a build identity after restart).");
                break;
            case RunnerRefreshOutcome.StaleRunnerRuntime stale:
                _err.WriteLine($"Runner runtime verification: stale-runner-runtime (runner buildGitHash {stale.ReportedHash ?? "<null>"} != repo HEAD {stale.RepoHeadHash ?? "<unavailable>"}).");
                break;
            case RunnerRefreshOutcome.Skipped skipped:
                _out.WriteLine($"Runner runtime verification: runner-refresh-skipped({skipped.Reason}).");
                break;
        }
    }

    private async Task<RunnerRefreshOutcome> VerifyRunnerRuntimeAsync(string repoRoot)
    {
        var repoHead = await TryReadRepoHeadAsync(repoRoot);
        var hostname = _getLocalHostname();
        if (string.IsNullOrWhiteSpace(hostname))
        {
            return new RunnerRefreshOutcome.UnknownIdentity("local hostname is unavailable; cannot identify local runner");
        }

        using var cts = new CancellationTokenSource(_runnerIdentityTimeout);
        RunnerIdentityView? identity = null;
        while (!cts.IsCancellationRequested)
        {
            identity = await TryReadRunnerIdentityAsync(hostname, cts.Token);
            if (identity is not null)
                break;
            try
            {
                await Task.Delay(RunnerIdentityPollInterval, cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        if (identity is null)
        {
            await VerifyRunnerDistManifestAsync(repoRoot, repoHead, "runner did not reconnect within the verification window");
            return RunnerRefreshOutcome.NotReconnected.Instance;
        }

        if (identity.Status != "online")
        {
            return new RunnerRefreshOutcome.StaleRunnerRuntime(
                ReportedHash: identity.BuildGitHash,
                RepoHeadHash: repoHead,
                Reason: $"runner reported status '{identity.Status}' instead of 'online'");
        }

        if (repoHead is null)
        {
            return new RunnerRefreshOutcome.UnknownIdentity("git rev-parse HEAD is unavailable; cannot compare identity");
        }

        if (string.IsNullOrWhiteSpace(identity.BuildGitHash))
        {
            return new RunnerRefreshOutcome.UnknownIdentity("runner did not report a buildGitHash; pre-T-001 runner cannot be verified");
        }

        return string.Equals(identity.BuildGitHash, repoHead, StringComparison.Ordinal)
            ? RunnerRefreshOutcome.Current.Instance
            : new RunnerRefreshOutcome.StaleRunnerRuntime(
                ReportedHash: identity.BuildGitHash,
                RepoHeadHash: repoHead,
                Reason: "reported buildGitHash differs from repo HEAD");
    }

    private async Task<RunnerRefreshOutcome> VerifyRunnerDistManifestAsync(
        string repoRoot,
        string? repoHead,
        string notReconnectedReason)
    {
        var manifestPath = Path.Combine(repoRoot, RunnerDistBuildInfoRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!_fileSystem.Exists(manifestPath))
        {
            return new RunnerRefreshOutcome.UnknownIdentity(
                $"runner did not reconnect; dist/build-info.json not found at {manifestPath}");
        }

        string? manifestHash = null;
        try
        {
            using var stream = _fileSystem.OpenRead(manifestPath);
            using var reader = new StreamReader(stream);
            var raw = await reader.ReadToEndAsync();
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("gitHash", out var element) && element.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                manifestHash = element.GetString();
            }
        }
        catch (Exception ex)
        {
            return new RunnerRefreshOutcome.UnknownIdentity(
                $"runner did not reconnect; could not read dist/build-info.json: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(manifestHash) || repoHead is null)
        {
            return new RunnerRefreshOutcome.UnknownIdentity(
                $"runner did not reconnect and dist/build-info.json is missing gitHash ({manifestPath})");
        }

        return string.Equals(manifestHash, repoHead, StringComparison.Ordinal)
            ? RunnerRefreshOutcome.Current.Instance
            : new RunnerRefreshOutcome.StaleRunnerRuntime(
                ReportedHash: manifestHash,
                RepoHeadHash: repoHead,
                Reason: notReconnectedReason + "; dist/build-info.json differs from repo HEAD");
    }

    private async Task<string?> TryReadRepoHeadAsync(string repoRoot)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var (code, stdout, _) = await _commandExecutor.ExecuteAsync("git", ["rev-parse", "HEAD"], repoRoot).WaitAsync(cts.Token);
            if (code != 0) return null;
            var hash = stdout.Trim();
            return string.IsNullOrWhiteSpace(hash) ? null : hash;
        }
        catch
        {
            return null;
        }
    }

    private async Task<RunnerIdentityView?> TryReadRunnerIdentityAsync(string hostname, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync($"/api/runner/identity?hostname={Uri.EscapeDataString(hostname)}", ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (!response.IsSuccessStatusCode) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await System.Text.Json.JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return null;
            return ReadRunnerIdentityView(data);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static RunnerIdentityView ReadRunnerIdentityView(System.Text.Json.JsonElement data)
    {
        string? GetString(string property)
        {
            if (!data.TryGetProperty(property, out var el) || el.ValueKind == System.Text.Json.JsonValueKind.Null)
                return null;
            return el.ValueKind == System.Text.Json.JsonValueKind.String ? el.GetString() : el.ToString();
        }
        DateTimeOffset? GetDate(string property)
        {
            if (!data.TryGetProperty(property, out var el) || el.ValueKind == System.Text.Json.JsonValueKind.Null)
                return null;
            if (el.ValueKind == System.Text.Json.JsonValueKind.String && DateTimeOffset.TryParse(el.GetString(), out var parsed))
                return parsed;
            return null;
        }
        return new RunnerIdentityView(
            GetString("runnerId") ?? string.Empty,
            GetString("hostname") ?? string.Empty,
            GetString("buildGitHash"),
            GetString("status") ?? "offline",
            GetDate("lastHeartbeatAt"),
            GetString("connectionState") ?? "disconnected");
    }

    private sealed record RunnerIdentityView(
        string RunnerId,
        string Hostname,
        string? BuildGitHash,
        string Status,
        DateTimeOffset? LastHeartbeatAt,
        string ConnectionState);

    private static string ResolveRepoRoot(string? explicitRoot)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot))
            return explicitRoot.Replace('\\', '/');

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Mohist.sln")))
                return dir.FullName.Replace('\\', '/');
            dir = dir.Parent;
        }

        return Directory.GetCurrentDirectory().Replace('\\', '/');
    }

    private async Task<string?> ResolveCliPathAsync(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return Path.GetFullPath(explicitPath);

        var envPath = _environment.GetEnvironmentVariable(CliPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(envPath))
            return Path.GetFullPath(envPath);

        var (exitCode, stdout, _) = await _commandExecutor.ExecuteAsync("sh", ["-lc", "command -v mo"], null);
        return exitCode == 0 ? stdout.Trim() : null;
    }

    public const string CliPathEnvironmentVariable = "MOHIST_CLI_PATH";

    private static string RuntimeIdentifier()
    {
        if (OperatingSystem.IsLinux()) return "linux-x64";
        if (OperatingSystem.IsMacOS()) return "osx-x64";
        if (OperatingSystem.IsWindows()) return "win-x64";
        return "linux-x64";
    }

    private static string RestartCommandLine(string kind)
    {
        var unitName = kind == "runner" ? "mohist-runner.service" : "mohist.service";
        if (OperatingSystem.IsWindows())
            return $"schtasks /Run /TN Mohist_{char.ToUpperInvariant(kind[0])}{kind[1..]}";
        return $"systemctl --user restart {unitName}";
    }

    private void WriteCommandFailureOutput(string stdout, string stderr)
    {
        if (!string.IsNullOrWhiteSpace(stdout))
            _err.WriteLine(stdout.TrimEnd());
        if (!string.IsNullOrWhiteSpace(stderr))
            _err.WriteLine(stderr.TrimEnd());
    }

    private async Task<ServerReadinessResult> WaitForServerReadyWithProgressAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        string? lastFailure = null;
        string? lastReason = null;
        var lastProgress = DateTimeOffset.UtcNow;
        int i = 0;

        while (!cts.IsCancellationRequested)
        {
            i++;
            var probe = new ReadinessProbeState();
            try
            {
                lastFailure = await CheckServerReadyOnceWithReasonAsync(cts.Token, probe);
                if (lastFailure is null)
                    return new ServerReadinessResult(true, null);
                lastReason = probe.Reason;
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                lastFailure = FormatReadinessException(ex);
                lastReason ??= "waiting for Mohist API";
            }

            if (DateTimeOffset.UtcNow - lastProgress >= ServerReadyProgressInterval)
            {
                _out.WriteLine($"  waiting... {lastReason ?? "waiting for Mohist API"}");
                lastProgress = DateTimeOffset.UtcNow;
            }

            try
            {
                await Task.Delay(ServerReadyPollInterval, cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return new ServerReadinessResult(false, lastFailure);
    }

    private async Task<ServerReadinessResult> WaitForServerReadyAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        string? lastFailure = null;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        int i = 0;
        while (!cts.IsCancellationRequested)
        {
            i++;
            try
            {
                lastFailure = await CheckServerReadyOnceAsync(cts.Token);
                if (lastFailure is null)
                    return new ServerReadinessResult(true, null);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                lastFailure = FormatReadinessException(ex);
                // The service can be active before Kestrel starts accepting requests.
            }

            try
            {
                await Task.Delay(ServerReadyPollInterval, cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return new ServerReadinessResult(false, lastFailure);
    }

    private async Task<string?> CheckServerReadyOnceWithReasonAsync(CancellationToken ct, ReadinessProbeState state)
    {
        using var health = await _http.GetAsync("/api/health", HttpCompletionOption.ResponseHeadersRead, ct);
        if (!health.IsSuccessStatusCode)
        {
            state.Reason = "waiting for Mohist API";
            return FormatReadinessStatus("/api/health", health.StatusCode);
        }

        using var index = await _http.GetAsync("/", HttpCompletionOption.ResponseHeadersRead, ct);
        if (!index.IsSuccessStatusCode)
        {
            state.Reason = "waiting for Web assets";
            return FormatReadinessStatus("/", index.StatusCode);
        }

        var contentType = index.Content.Headers.ContentType?.MediaType;
        if (!string.Equals(contentType, "text/html", StringComparison.OrdinalIgnoreCase))
        {
            state.Reason = "waiting for Web assets";
            return $"GET / returned content type {contentType ?? "unknown"}, expected text/html";
        }

        var html = await index.Content.ReadAsStringAsync(ct);
        var assetPath = FindFirstAssetPath(html);
        if (assetPath is null)
        {
            state.Reason = "waiting for Web assets";
            return "GET / did not reference a /assets/* bundle";
        }

        using var asset = await _http.GetAsync(assetPath, HttpCompletionOption.ResponseHeadersRead, ct);
        if (asset.StatusCode != HttpStatusCode.OK)
        {
            state.Reason = "waiting for Web assets";
            return FormatReadinessStatus(assetPath, asset.StatusCode);
        }

        state.Reason = null;
        return null;
    }

    private async Task<string?> CheckServerReadyOnceAsync(CancellationToken ct)
    {
        using var health = await _http.GetAsync("/api/health", HttpCompletionOption.ResponseHeadersRead, ct);
        if (!health.IsSuccessStatusCode)
            return FormatReadinessStatus("/api/health", health.StatusCode);

        using var index = await _http.GetAsync("/", HttpCompletionOption.ResponseHeadersRead, ct);
        if (!index.IsSuccessStatusCode)
            return FormatReadinessStatus("/", index.StatusCode);

        var contentType = index.Content.Headers.ContentType?.MediaType;
        if (!string.Equals(contentType, "text/html", StringComparison.OrdinalIgnoreCase))
            return $"GET / returned content type {contentType ?? "unknown"}, expected text/html";

        var html = await index.Content.ReadAsStringAsync(ct);
        var assetPath = FindFirstAssetPath(html);
        if (assetPath is null)
            return "GET / did not reference a /assets/* bundle";

        using var asset = await _http.GetAsync(assetPath, HttpCompletionOption.ResponseHeadersRead, ct);
        return asset.StatusCode == HttpStatusCode.OK ? null : FormatReadinessStatus(assetPath, asset.StatusCode);
    }

    private sealed record ServerReadinessResult(bool Ready, string? LastFailure);

    private sealed class ReadinessProbeState
    {
        public string? Reason { get; set; }
    }

    private sealed record StageOutcome(bool Success, int ExitCode, Exception? Exception);

    private static class StageLabels
    {
        public const string CliUpdate = "Updating CLI";
        public const string PrepareRunner = "Preparing workflow runner";
        public const string UpdateServer = "Updating Mohist Server";
        public const string WaitingForReady = "Waiting for Mohist to become usable";
        public const string RestoreRunner = "Restoring workflow runner";
        public const string VerifyRuntime = "Verifying workflow runtime";
    }

    private sealed class SystemInfoRunningSnapshot
    {
        [System.Text.Json.Serialization.JsonPropertyName("gitHash")]
        public string? GitHash { get; set; }
    }

    private sealed class SystemInfoServiceSnapshot
    {
        [System.Text.Json.Serialization.JsonPropertyName("runner")]
        public string? Runner { get; set; }
    }

    private sealed class SystemInfoSnapshot
    {
        public static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        [System.Text.Json.Serialization.JsonPropertyName("running")]
        public SystemInfoRunningSnapshot? Running { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("services")]
        public SystemInfoServiceSnapshot? Services { get; set; }
    }

    private static string FormatReadinessStatus(string path, HttpStatusCode statusCode)
        => $"GET {path} returned {(int)statusCode} {statusCode}";

    private static string FormatReadinessException(Exception ex)
    {
        var message = ex.InnerException is null
            ? ex.Message
            : $"{ex.Message} ({ex.InnerException.Message})";
        return $"{ex.GetType().Name}: {message}";
    }

    private static string? FindFirstAssetPath(string html)
    {
        var match = Regex.Match(
            html,
            """(?:src|href)=["'](?<path>/assets/[^"']+)["']""",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["path"].Value : null;
    }
}

internal abstract record RunnerRefreshOutcome
{
    private RunnerRefreshOutcome() { }

    public int ExitCode => this switch
    {
        StaleRunnerRuntime => 1,
        NotReconnected => 1,
        _ => 0,
    };

    /// <summary>Reported build identity matches the repo HEAD.</summary>
    public sealed record Current : RunnerRefreshOutcome
    {
        public static readonly Current Instance = new();
        private Current() { }
    }

    /// <summary>Runner runtime identity could not be determined.</summary>
    public sealed record UnknownIdentity(string Reason) : RunnerRefreshOutcome;

    /// <summary>Runner did not report a fresh build identity after the restart window.</summary>
    public sealed record NotReconnected : RunnerRefreshOutcome
    {
        public static readonly NotReconnected Instance = new();
        private NotReconnected() { }
    }

    /// <summary>Runner is online but reported build identity differs from the current source.</summary>
    public sealed record StaleRunnerRuntime(string? ReportedHash, string? RepoHeadHash, string Reason) : RunnerRefreshOutcome;

    /// <summary>Runner refresh was intentionally skipped before build/restart.</summary>
    public sealed record Skipped(string Reason) : RunnerRefreshOutcome;
}
