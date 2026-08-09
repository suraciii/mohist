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
        var updater = MohistCliCommands.ResolveSourceCodeUpdater(provider);
        var repoRootOpt = new Option<string?>("--repo-root") { Description = "Repository root path" };
        var cliPathOpt = new Option<string?>("--cli-path") { Description = "mo executable path" };
        var dryRunOpt = MohistCliCommands.DryRunOption();
        var continueAfterCliUpdateOpt = new Option<bool>("--continue-after-cli-update")
        {
            Description = "Internal: continue the update after the CLI self-update stage",
            Hidden = true,
        };

        update.Options.Add(repoRootOpt);
        update.Options.Add(cliPathOpt);
        update.Options.Add(dryRunOpt);
        update.Options.Add(continueAfterCliUpdateOpt);
        update.SetAction(async (ctx, token) =>
        {
            var repoRoot = ctx.GetValue(repoRootOpt);
            var cliPath = ctx.GetValue(cliPathOpt);
            var dryRun = ctx.GetValue(dryRunOpt);
            var continueAfterCliUpdate = ctx.GetValue(continueAfterCliUpdateOpt);
            return await updater.UpdateAllAsync(repoRoot, dryRun, cliPath, token, continueAfterCliUpdate);
        });

        update.Subcommands.Add(BuildCliUpdate(updater));
        update.Subcommands.Add(BuildServerUpdate(updater));
        update.Subcommands.Add(BuildRunnerUpdate(updater));
        update.Subcommands.Add(BuildSlackUpdate(updater));

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

    private static Command BuildSlackUpdate(SourceCodeUpdater updater)
    {
        var cmd = new Command("slack", "Update the mohist-slack adapter from source");
        var repoRootOpt = new Option<string?>("--repo-root") { Description = "Repository root path" };
        var dryRunOpt = MohistCliCommands.DryRunOption();
        cmd.Options.Add(repoRootOpt);
        cmd.Options.Add(dryRunOpt);
        cmd.SetAction(async (ctx, token) => await updater.UpdateSlackAsync(
            ctx.GetValue(repoRootOpt), ctx.GetValue(dryRunOpt), token));
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
internal partial class SourceCodeUpdater
{
    private static readonly TimeSpan ServerReadyTimeout = TimeSpan.FromSeconds(180);

    private readonly TextWriter _out;
    private readonly TextWriter _err;
    private readonly TimeSpan _serverReadyTimeout;
    private readonly UpdateOperations _operations;
    private readonly RuntimeConsistencyValidator _validator;
    private readonly ServiceReadinessProbe _readinessProbe;
    private readonly RunnerRefreshVerifier _runnerRefreshVerifier;
    private readonly UpdateOutcomeReporter _outcomeReporter;
    private readonly TimeProvider _timeProvider;

    public SourceCodeUpdater(
        TextWriter output,
        TextWriter error,
        UpdateOperations operations,
        RuntimeConsistencyValidator validator,
        ServiceReadinessProbe readinessProbe,
        RunnerRefreshVerifier runnerRefreshVerifier,
        UpdateOutcomeReporter outcomeReporter,
        TimeSpan? serverReadyTimeout = null,
        TimeProvider? timeProvider = null)
    {
        _out = output;
        _err = error;
        _operations = operations;
        _validator = validator;
        _readinessProbe = readinessProbe;
        _runnerRefreshVerifier = runnerRefreshVerifier;
        _outcomeReporter = outcomeReporter;
        _serverReadyTimeout = serverReadyTimeout ?? ServerReadyTimeout;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public const string ServerUrlEnvironmentVariable = "MOHIST_SERVER_URL";
    public const string CliPathEnvironmentVariable = "MOHIST_CLI_PATH";

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
        TimeSpan? runnerIdentityPollInterval = null,
        Func<string?>? getLocalHostname = null,
        string? unitDir = null,
        TimeProvider? timeProvider = null,
        IRuntimeUpdateLeaseProvider? runtimeUpdateLeases = null,
        IRunnerRuntimeReadinessSignal? runnerReadinessSignal = null)
    {
        var fs = fileSystem ?? RealFileSystem.Instance;
        var env = environment ?? SystemEnvironmentVariableProvider.Instance;
        var httpClient = http ?? new HttpClient
        {
            BaseAddress = new Uri(env.GetEnvironmentVariable(ServerUrlEnvironmentVariable) ?? "http://127.0.0.1:3456"),
            Timeout = TimeSpan.FromSeconds(5),
        };
        var operations = new UpdateOperations(
            output,
            error,
            systemd,
            commandExecutor,
            fs,
            env,
            unitDir,
            getUserHome,
            runtimeUpdateLeases);
        var validator = new RuntimeConsistencyValidator(
            httpClient,
            commandExecutor,
            fs,
            env,
            output,
            getUserHome);
        var readinessProbe = new ServiceReadinessProbe(httpClient, output, timeProvider);
        var runnerRefreshVerifier = new RunnerRefreshVerifier(
            httpClient,
            commandExecutor,
            fs,
            runnerIdentityTimeout: runnerIdentityTimeout,
            timeProvider: timeProvider,
            readinessSignal: runnerReadinessSignal);
        var outcomeReporter = new UpdateOutcomeReporter(httpClient, output);
        return new SourceCodeUpdater(
            output,
            error,
            operations,
            validator,
            readinessProbe,
            runnerRefreshVerifier,
            outcomeReporter,
            serverReadyTimeout,
            timeProvider);
    }

    internal RuntimeConsistencyValidator Validator => _validator;
    internal ServiceReadinessProbe ReadinessProbe => _readinessProbe;
    internal RunnerRefreshVerifier RunnerRefreshVerifier => _runnerRefreshVerifier;

    public async Task<int> SyncSkillsAsync(string? repoRoot, string? sourceSkillData, bool dryRun, CancellationToken cancellationToken = default)
    {
        return await _operations.SyncSkillsAsync(repoRoot, sourceSkillData, dryRun, cancellationToken);
    }

    public virtual async Task<int> UpdateAllAsync(
        string? repoRoot,
        bool dryRun,
        string? cliPath = null,
        CancellationToken cancellationToken = default,
        bool continueAfterCliUpdate = false)
    {
        var resolvedCliPath = await ResolveCliPathAsync(cliPath);
        var context = new UpdateContext(dryRun, repoRoot, resolvedCliPath, cancellationToken);
        if (string.IsNullOrWhiteSpace(resolvedCliPath))
        {
            _err.WriteLine("Could not resolve mo executable path. Pass --cli-path to update the CLI explicitly.");
            return await FinalizeAsync(context, 1);
        }

        if (!continueAfterCliUpdate)
        {
            var cliOutcome = await RunStageMachineAsync(context, async (ctx, token) =>
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

            if (!cliOutcome.Success)
            {
                return await FinalizeAsync(context, cliOutcome.ExitCode);
            }

            if (!context.DryRun)
            {
                return await ContinueWithUpdatedCliAsync(context);
            }
        }

        return await RunPostCliUpdateStagesAsync(context);
    }

    private async Task<int> RunPostCliUpdateStagesAsync(UpdateContext context)
    {
        if (!context.DryRun)
        {
            var manager = await _operations.ProbeRuntimeManagerAsync(context.CancellationToken);
            if (!manager.Available)
            {
                var reason = manager.Reason ?? "unknown reason";
                _err.WriteLine($"Managed runtime update cannot start because the service manager is unavailable: {reason}. No runtime was changed.");
                context.RecordStage("Probing service manager", $"unavailable: {reason}");
                return await FinalizeAsync(context, 1);
            }
            context.RuntimeManager = manager;

            var source = await _operations.ResolveUpdateSourceAsync(context.RepoRoot, context.CancellationToken);
            if (source is null)
                return await FinalizeAsync(context, 1);

            context.UpdateSource = source;
            context.SourceHead = source.Hash;
            context.RecordStage("Resolving update source", $"resolved {source.Hash}");
        }

        var outcome = await RunStageMachineAsync(context, async (ctx, token) =>
        {
            return await PrepareRunnerStageAsync(ctx, token);
        });

        if (context.Interrupted && !context.RunnerStopped)
        {
            _out.WriteLine("Update cancelled before the runner was stopped. No recovery needed.");
            return await FinalizeAsync(context, 130);
        }

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
            return await PrepareRunnerCandidateStageAsync(ctx, token);
        });

        if (context.Interrupted || !outcome.Success)
        {
            return await FinalizeAsync(context, outcome.ExitCode);
        }

        outcome = await RunStageMachineAsync(context, async (ctx, token) =>
        {
            return await StopRunnerStageAsync(ctx, token);
        });

        if (context.Interrupted || !outcome.Success)
        {
            return await FinalizeAsync(context, outcome.ExitCode);
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

    private async Task<string?> ResolveCliPathAsync(string? explicitPath)
    {
        return await _operations.ResolveCliPathAsync(explicitPath);
    }

    private async Task<int> ContinueWithUpdatedCliAsync(UpdateContext context)
    {
        if (string.IsNullOrWhiteSpace(context.CliPath))
        {
            _err.WriteLine("CLI was updated, but the mo executable path is no longer known. Run 'mo update' again to finish.");
            return await FinalizeAsync(context, 1);
        }

        _out.WriteLine("Continuing update with the refreshed CLI process.");

        var args = new List<string>
        {
            "update",
            "--continue-after-cli-update",
            "--cli-path",
            context.CliPath!,
        };

        if (!string.IsNullOrWhiteSpace(context.RepoRoot))
        {
            args.Add("--repo-root");
            args.Add(context.RepoRoot!);
        }

        var root = _operations.ResolveRepoRoot(context.RepoRoot);
        var (exitCode, stdout, stderr) = await _operations.ExecuteCommandAsync(
            context.CliPath!,
            args.ToArray(),
            root,
            context.CancellationToken);

        if (!string.IsNullOrWhiteSpace(stdout))
            _out.Write(stdout);
        if (!string.IsNullOrWhiteSpace(stderr))
            _err.Write(stderr);

        if (exitCode != 0)
        {
            _err.WriteLine("The refreshed CLI process did not complete the update successfully.");
        }

        return exitCode;
    }


    private async Task<int> FinalizeAfterServerAsync(UpdateContext context, StageOutcome outcome)
    {
        return await FinalizeAsync(context, outcome.ExitCode);
    }

    private sealed record StageOutcome(bool Success, int ExitCode, Exception? Exception);

    private static class StageLabels
    {
        public const string CliUpdate = "Updating CLI";
        public const string PrepareRunner = "Preparing workflow runner";
        public const string UpdateServer = "Updating Mohist Server";
        public const string WaitingForReady = "Waiting for Mohist to become usable";
        public const string PrepareRunnerCandidate = "Preparing validated workflow runner candidate";
        public const string StopRunner = "Stopping current workflow runner";
        public const string RestoreRunner = "Restoring workflow runner";
        public const string VerifyRuntime = "Verifying workflow runtime";
    }
}
