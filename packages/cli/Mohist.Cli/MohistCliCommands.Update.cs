using System.CommandLine;
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
        update.SetAction(async ctx =>
        {
            var repoRoot = ctx.GetValue(repoRootOpt);
            var cliPath = ctx.GetValue(cliPathOpt);
            var dryRun = ctx.GetValue(dryRunOpt);
            return await updater.UpdateAllAsync(repoRoot, dryRun, cliPath);
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
        cmd.SetAction(async ctx =>
        {
            var dryRun = ctx.GetValue(dryRunOpt);
            var repoRoot = ctx.GetValue(repoRootOpt);
            var cliPath = ctx.GetValue(cliPathOpt);
            return await updater.UpdateCliAsync(repoRoot, dryRun, cliPath);
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
        cmd.SetAction(async ctx =>
        {
            var dryRun = ctx.GetValue(dryRunOpt);
            var repoRoot = ctx.GetValue(repoRootOpt);
            return await updater.UpdateServerAsync(repoRoot, dryRun);
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
        cmd.SetAction(async ctx =>
        {
            var dryRun = ctx.GetValue(dryRunOpt);
            var repoRoot = ctx.GetValue(repoRootOpt);
            return await updater.UpdateRunnerAsync(repoRoot, dryRun);
        });
        return cmd;
    }
}

internal sealed class SourceCodeUpdater
{
    private static readonly TimeSpan ServerReadyTimeout = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan ServerReadyPollInterval = TimeSpan.FromMilliseconds(500);

    private readonly TextWriter _out;
    private readonly TextWriter _err;
    private readonly SystemdServiceInstaller _systemd;
    private readonly ICommandExecutor _commandExecutor;
    private readonly HttpClient _http;
    private readonly TimeSpan _serverReadyTimeout;

    public SourceCodeUpdater(
        TextWriter output,
        TextWriter error,
        SystemdServiceInstaller systemd,
        ICommandExecutor commandExecutor,
        HttpClient? http = null,
        TimeSpan? serverReadyTimeout = null)
    {
        _out = output;
        _err = error;
        _systemd = systemd;
        _commandExecutor = commandExecutor;
        _http = http ?? new HttpClient
        {
            BaseAddress = new Uri(Environment.GetEnvironmentVariable("MOHIST_SERVER_URL") ?? "http://localhost:3456"),
            Timeout = TimeSpan.FromSeconds(5),
        };
        _serverReadyTimeout = serverReadyTimeout ?? ServerReadyTimeout;
    }

    public async Task<int> UpdateAllAsync(string? repoRoot, bool dryRun, string? cliPath = null)
    {
        var root = ResolveRepoRoot(repoRoot);
        _out.WriteLine($"Updating Mohist from source: {root}");

        var cli = await UpdateCliAsync(root, dryRun, cliPath);
        if (cli != 0) return cli;

        var stoppedRunner = await StopRunnerForServerUpdateAsync(dryRun);
        if (stoppedRunner != 0) return stoppedRunner;

        var server = await UpdateServerAsync(root, dryRun);
        if (server != 0)
            return await RestoreRunnerAfterFailedUpdateAsync(dryRun, server);

        var runner = await UpdateRunnerAsync(root, dryRun);
        if (runner != 0)
            return await RestoreRunnerAfterFailedUpdateAsync(dryRun, runner);

        return 0;
    }

    private async Task<int> StopRunnerForServerUpdateAsync(bool dryRun)
    {
        _out.WriteLine("Stopping runner service before server restart.");
        var stop = await _systemd.StopRunnerAsync(new ServiceCommandOptions(dryRun, null, 100, false));
        if (stop != 0)
        {
            _err.WriteLine("Warning: Failed to stop runner service before server restart.");
            return stop;
        }

        return 0;
    }

    private async Task<int> RestoreRunnerAfterFailedUpdateAsync(bool dryRun, int originalExitCode)
    {
        _err.WriteLine("Update failed after runner service was stopped. Restoring runner service.");
        var start = await _systemd.StartRunnerAsync(new ServiceCommandOptions(dryRun, null, 100, false));
        if (start != 0)
            _err.WriteLine("Warning: Failed to restore runner service. You may need to start it manually.");

        return originalExitCode;
    }

    public async Task<int> UpdateCliAsync(string? repoRoot, bool dryRun, string? cliPath = null)
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

        _out.WriteLine($"Updating CLI from source: {root}");

        if (dryRun)
        {
            _out.WriteLine("Dry run: would execute:");
            _out.WriteLine($"  cd {root} && dotnet publish packages/cli/Mohist.Cli/Mohist.Cli.csproj -c Release -r {RuntimeIdentifier()} --self-contained true /p:PublishSingleFile=true -o {publishDir}");
            _out.WriteLine($"  cp {binary} {tempTarget}");
            _out.WriteLine($"  chmod +x {tempTarget}");
            _out.WriteLine($"  mv {tempTarget} {target}");
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
        var (publish, _, publishErr) = await _commandExecutor.ExecuteAsync("dotnet", publishArgs, root);
        if (publish != 0)
        {
            if (!string.IsNullOrWhiteSpace(publishErr)) _err.WriteLine(publishErr);
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

        _out.WriteLine($"CLI updated: {target}");
        return 0;
    }

    public async Task<int> UpdateServerAsync(string? repoRoot, bool dryRun)
    {
        var root = ResolveRepoRoot(repoRoot);

        _out.WriteLine($"Updating server from source: {root}");

        if (dryRun)
        {
            _out.WriteLine("Dry run: would execute:");
            _out.WriteLine($"  cd {root} && dotnet build Mohist.sln");
            _out.WriteLine("  systemctl --user restart mohist.service (if installed)");
            return 0;
        }

        var (build, _, buildErr) = await _commandExecutor.ExecuteAsync("dotnet", ["build", "Mohist.sln"], root);
        if (build != 0)
        {
            if (!string.IsNullOrWhiteSpace(buildErr)) _err.WriteLine(buildErr);
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

        _out.WriteLine("Server service restarted.");
        var ready = await WaitForServerReadyAsync(_serverReadyTimeout);
        if (!ready)
        {
            _err.WriteLine($"Server service restarted, but /api/health did not become ready within {(int)_serverReadyTimeout.TotalSeconds} seconds.");
            return 1;
        }

        _out.WriteLine("Server is ready.");
        return 0;
    }

    public async Task<int> UpdateRunnerAsync(string? repoRoot, bool dryRun)
    {
        var root = ResolveRepoRoot(repoRoot);

        _out.WriteLine($"Updating runner from source: {root}");

        if (dryRun)
        {
            _out.WriteLine("Dry run: would execute:");
            _out.WriteLine($"  cd {root} && npm run build -w packages/runner");
            _out.WriteLine("  systemctl --user restart mohist-runner.service (if installed)");
            return 0;
        }

        var (build, _, buildErr) = await _commandExecutor.ExecuteAsync("npm", ["run", "build", "-w", "packages/runner"], root);
        if (build != 0)
        {
            if (!string.IsNullOrWhiteSpace(buildErr)) _err.WriteLine(buildErr);
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
        return 0;
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
            return Path.GetFullPath(explicitPath);

        var envPath = Environment.GetEnvironmentVariable("MOHIST_CLI_PATH");
        if (!string.IsNullOrWhiteSpace(envPath))
            return Path.GetFullPath(envPath);

        var (exitCode, stdout, _) = await _commandExecutor.ExecuteAsync("sh", ["-lc", "command -v mo"], null);
        return exitCode == 0 ? stdout.Trim() : null;
    }

    private static string RuntimeIdentifier()
    {
        if (OperatingSystem.IsLinux()) return "linux-x64";
        if (OperatingSystem.IsMacOS()) return "osx-x64";
        if (OperatingSystem.IsWindows()) return "win-x64";
        return "linux-x64";
    }

    private async Task<bool> WaitForServerReadyAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!cts.IsCancellationRequested)
        {
            try
            {
                using var response = await _http.GetAsync("/api/health", cts.Token);
                if (response.IsSuccessStatusCode)
                    return true;
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                break;
            }
            catch
            {
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

        return false;
    }
}
