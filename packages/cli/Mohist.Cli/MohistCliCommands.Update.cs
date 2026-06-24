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
internal sealed partial class SourceCodeUpdater
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
