using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
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

/// <summary>
/// Update orchestration facade. Drives the stage machine across all update entry points and
/// delegates runtime consistency checks to <see cref="RuntimeConsistencyValidator"/>, service
/// readiness polling to <see cref="ServiceReadinessProbe"/>, and runner refresh verification
/// to <see cref="RunnerRefreshVerifier"/>. The facade itself only owns stage orchestration,
/// finalization, and small resolution helpers.
/// </summary>
internal sealed class SourceCodeUpdater
{
    private static readonly TimeSpan ServerReadyTimeout = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan RunnerActivePollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RunnerActiveTimeout = TimeSpan.FromSeconds(30);

    private readonly TextWriter _out;
    private readonly TextWriter _err;
    private readonly IServiceInstaller _systemd;
    private readonly ICommandExecutor _commandExecutor;
    private readonly IFileSystem _fileSystem;
    private readonly IEnvironmentVariableProvider _environment;
    private readonly HttpClient _http;
    private readonly TimeSpan _serverReadyTimeout;
    private readonly string? _unitDir;
    private readonly Func<string?>? _getUserHome;
    private readonly RuntimeConsistencyValidator _validator;
    private readonly ServiceReadinessProbe _readinessProbe;
    private readonly RunnerRefreshVerifier _runnerRefreshVerifier;

    public SourceCodeUpdater(
        TextWriter output,
        TextWriter error,
        IServiceInstaller systemd,
        ICommandExecutor commandExecutor,
        IFileSystem fileSystem,
        IEnvironmentVariableProvider environment,
        HttpClient http,
        RuntimeConsistencyValidator validator,
        ServiceReadinessProbe readinessProbe,
        RunnerRefreshVerifier runnerRefreshVerifier,
        TimeSpan? serverReadyTimeout = null,
        string? unitDir = null,
        Func<string?>? getUserHome = null)
    {
        _out = output;
        _err = error;
        _systemd = systemd;
        _commandExecutor = commandExecutor;
        _fileSystem = fileSystem;
        _environment = environment;
        _http = http;
        _validator = validator;
        _readinessProbe = readinessProbe;
        _runnerRefreshVerifier = runnerRefreshVerifier;
        _serverReadyTimeout = serverReadyTimeout ?? ServerReadyTimeout;
        _unitDir = unitDir;
        _getUserHome = getUserHome;
    }

    public const string ServerUrlEnvironmentVariable = "MOHIST_SERVER_URL";
    public const string CliPathEnvironmentVariable = "MOHIST_CLI_PATH";

    /// <summary>
    /// Test/legacy bridge: builds a <see cref="SourceCodeUpdater"/> with the same
    /// 12-parameter shape callers used before the validator/probe/verifier collaborators
    /// were extracted. The new collaborators are constructed internally with defaults;
    /// callers that need to assert on a specific check or tune the readiness probe should
    /// construct the collaborators explicitly and use the primary constructor.
    /// </summary>
    internal static SourceCodeUpdater CreateWithDefaults(
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
        var fs = fileSystem ?? RealFileSystem.Instance;
        var env = environment ?? SystemEnvironmentVariableProvider.Instance;
        var httpClient = http ?? new HttpClient
        {
            BaseAddress = new Uri(env.GetEnvironmentVariable(ServerUrlEnvironmentVariable) ?? "http://127.0.0.1:3456"),
            Timeout = TimeSpan.FromSeconds(5),
        };
        var validator = new RuntimeConsistencyValidator(httpClient, commandExecutor, fs, env, output, getUserHome);
        var readinessProbe = new ServiceReadinessProbe(httpClient, output);
        var runnerRefreshVerifier = new RunnerRefreshVerifier(
            httpClient,
            commandExecutor,
            fs,
            getLocalHostname: getLocalHostname ?? (() => Environment.MachineName),
            runnerIdentityTimeout: runnerIdentityTimeout);
        return new SourceCodeUpdater(
            output,
            error,
            systemd,
            commandExecutor,
            fs,
            env,
            httpClient,
            validator,
            readinessProbe,
            runnerRefreshVerifier,
            serverReadyTimeout,
            unitDir,
            getUserHome);
    }

    internal string ResolveManagedSkillAssetRoot()
    {
        var home = _getUserHome?.Invoke();
        if (string.IsNullOrWhiteSpace(home))
            home = _environment.GetEnvironmentVariable(SkillAssetRootResolver.HomeEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(home))
            return Path.Combine(home, ".mohist", "cli", "skill-data");
        return Path.Combine(AppContext.BaseDirectory, "skill-data");
    }

    internal Uri? ServerBaseAddress => _http.BaseAddress;

    internal RuntimeConsistencyValidator Validator => _validator;
    internal ServiceReadinessProbe ReadinessProbe => _readinessProbe;
    internal RunnerRefreshVerifier RunnerRefreshVerifier => _runnerRefreshVerifier;

    public async Task<int> UpdateAllAsync(string? repoRoot, bool dryRun, string? cliPath = null, CancellationToken cancellationToken = default)
    {
        var resolvedCliPath = await ResolveCliPathAsync(cliPath);
        var context = new UpdateContext(dryRun, repoRoot, resolvedCliPath, cancellationToken);
        if (string.IsNullOrWhiteSpace(resolvedCliPath))
        {
            _err.WriteLine("Could not resolve mo executable path. Pass --cli-path to update the CLI explicitly.");
            return await FinalizeAsync(context, 1);
        }

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

    public async Task<int> UpdateCliAsync(string? repoRoot, bool dryRun, string? cliPath = null, CancellationToken cancellationToken = default)
    {
        var root = ResolveRepoRoot(repoRoot);
        var home = _getUserHome?.Invoke();
        var primaryTarget = ResolveManagedCliPath(home);
        var alternateTarget = ResolveAlternateManagedCliPath(home);
        var currentProcessPath = Environment.ProcessPath?.Replace('\\', '/');
        var managedTarget = string.Equals(currentProcessPath, primaryTarget, StringComparison.OrdinalIgnoreCase)
            ? alternateTarget
            : primaryTarget;
        var target = !string.IsNullOrWhiteSpace(cliPath) ? await ResolveCliPathAsync(cliPath) : managedTarget;
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
            if (target == primaryTarget || target == alternateTarget)
            {
                var wrapper = ResolveCliWrapperPath(home);
                _out.WriteLine($"  ensure wrapper script at {wrapper} -> {target}");
            }
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

        _fileSystem.CreateDirectory(Path.GetDirectoryName(target)!);

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

        if (target == primaryTarget || target == alternateTarget)
        {
            var wrapperExit = await EnsureCliWrapperAsync(target, home);
            if (wrapperExit != 0)
            {
                _err.WriteLine("CLI wrapper installation failed. Aborting update.");
                return wrapperExit;
            }
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
        var ready = await _readinessProbe.WaitForServerReadyAsync(_serverReadyTimeout, cancellationToken);
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
            _runnerRefreshVerifier.WriteSkippedSummary(reason, _out, _err);
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

        var outcome = await _runnerRefreshVerifier.VerifyRunnerRuntimeAsync(root);
        outcome.WriteSummary(_out, _err);
        return outcome.ExitCode;
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

        var exitCode = await UpdateCliAsync(context.RepoRoot, context.DryRun, null, token);
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
            _runnerRefreshVerifier.WriteSkippedSummary(reason, _out, _err);
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

        var ready = await _readinessProbe.WaitForServerReadyWithProgressAsync(_serverReadyTimeout, token);
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

        if (!context.DryRun)
        {
            _out.WriteLine("Waiting for runner service to become active...");
            using var activeCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            activeCts.CancelAfter(RunnerActiveTimeout);
            var becameActive = false;
            while (!activeCts.IsCancellationRequested)
            {
                if (await _systemd.IsRunnerRunningAsync(activeCts.Token))
                {
                    becameActive = true;
                    break;
                }

                try
                {
                    await Task.Delay(RunnerActivePollInterval, activeCts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            if (!becameActive)
            {
                context.RecordStage(StageLabels.RestoreRunner, "runner did not become active in time");
                context.UnavailableCapability ??= "Runner unavailable";
                return 1;
            }
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
            await _validator.CheckCliBinaryAsync(context, token),
            await _validator.CheckServerIdentityAsync(context, token),
            await _validator.CheckWebAssetsAsync(context, token),
            await _validator.CheckRunnerConnectionAsync(context, token),
            await _validator.CheckManagedSkillAssetsAsync(context, token),
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
            return explicitPath;

        var envPath = _environment.GetEnvironmentVariable(CliPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(envPath))
            return envPath;

        var home = _getUserHome?.Invoke();
        var wrapper = ResolveCliWrapperPath(home);
        if (_fileSystem.Exists(wrapper))
            return wrapper;

        var (exitCode, stdout, _) = await _commandExecutor.ExecuteAsync("sh", ["-lc", "command -v mo"], null);
        return exitCode == 0 ? stdout.Trim() : null;
    }

    private static string ResolveManagedCliPath(string? home = null)
    {
        var root = !string.IsNullOrWhiteSpace(home)
            ? home
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(root, ".local", "share", "mohist", "cli", "mo").Replace('\\', '/');
    }

    private static string ResolveAlternateManagedCliPath(string? home = null)
    {
        var root = !string.IsNullOrWhiteSpace(home)
            ? home
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(root, ".local", "share", "mohist", "cli", "mo.next").Replace('\\', '/');
    }

    private static string ResolveCliWrapperPath(string? home = null)
    {
        var root = !string.IsNullOrWhiteSpace(home)
            ? home
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(root, ".local", "bin", "mo").Replace('\\', '/');
    }

    private async Task<int> EnsureCliWrapperAsync(string managedCliPath, string? home = null)
    {
        var wrapperPath = ResolveCliWrapperPath(home);
        _fileSystem.CreateDirectory(Path.GetDirectoryName(wrapperPath)!);

        if (_fileSystem.Exists(wrapperPath))
        {
            try
            {
                _fileSystem.Delete(wrapperPath);
            }
            catch (Exception ex)
            {
                _err.WriteLine($"Could not remove existing entry at {wrapperPath}: {ex.Message}");
                return 1;
            }
        }

        var wrapper = "#!/bin/sh" + Environment.NewLine
            + $"exec \"{managedCliPath}\" \"$@\"" + Environment.NewLine;
        try
        {
            await _fileSystem.WriteAllTextAsync(wrapperPath, wrapper);
        }
        catch (Exception ex)
        {
            _err.WriteLine($"Could not write wrapper script at {wrapperPath}: {ex.Message}");
            return 1;
        }

        var (chmod, _, chmodErr) = await _commandExecutor.ExecuteAsync("chmod", ["+x", wrapperPath], null);
        if (chmod != 0)
        {
            if (!string.IsNullOrWhiteSpace(chmodErr)) _err.WriteLine(chmodErr);
            _err.WriteLine($"Could not make wrapper script at {wrapperPath} executable.");
            return chmod;
        }

        _out.WriteLine($"Installed CLI wrapper: {wrapperPath}");
        return 0;
    }

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
}
