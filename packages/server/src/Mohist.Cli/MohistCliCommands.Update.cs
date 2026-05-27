using System.CommandLine;
using System.CommandLine.Parsing;
using Microsoft.Extensions.DependencyInjection;

namespace Mohist.Cli;

internal static class UpdateCommands
{
    public static Command Build(IServiceProvider provider)
    {
        var update = new Command("update", "Update mohist components from source");
        var systemd = provider.GetRequiredService<SystemdServiceInstaller>();
        var updater = provider.GetRequiredService<SourceCodeUpdater>();

        update.Subcommands.Add(BuildServerUpdate(updater));
        update.Subcommands.Add(BuildRunnerUpdate(updater));

        return update;
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
    private readonly TextWriter _out;
    private readonly TextWriter _err;
    private readonly SystemdServiceInstaller _systemd;
    private readonly ICommandExecutor _commandExecutor;

    public SourceCodeUpdater(
        TextWriter output,
        TextWriter error,
        SystemdServiceInstaller systemd,
        ICommandExecutor commandExecutor)
    {
        _out = output;
        _err = error;
        _systemd = systemd;
        _commandExecutor = commandExecutor;
    }

    public async Task<int> UpdateServerAsync(string? repoRoot, bool dryRun)
    {
        var root = ResolveRepoRoot(repoRoot);

        _out.WriteLine($"Updating server from source: {root}");

        if (dryRun)
        {
            _out.WriteLine("Dry run: would execute:");
            _out.WriteLine($"  cd {root} && git pull");
            _out.WriteLine($"  cd {root} && dotnet build Mohist.sln");
            _out.WriteLine("  systemctl --user restart mohist.service (if installed)");
            return 0;
        }

        var (gitPull, _, gitPullErr) = await _commandExecutor.ExecuteAsync("git", ["pull"], root);
        if (gitPull != 0)
        {
            if (!string.IsNullOrWhiteSpace(gitPullErr)) _err.WriteLine(gitPullErr);
            _err.WriteLine("Git pull failed. Aborting update.");
            return gitPull;
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
        return 0;
    }

    public async Task<int> UpdateRunnerAsync(string? repoRoot, bool dryRun)
    {
        var root = ResolveRepoRoot(repoRoot);

        _out.WriteLine($"Updating runner from source: {root}");

        if (dryRun)
        {
            _out.WriteLine("Dry run: would execute:");
            _out.WriteLine($"  cd {root} && git pull");
            _out.WriteLine($"  cd {root} && npm run build -w packages/runner");
            _out.WriteLine("  systemctl --user restart mohist-runner.service (if installed)");
            return 0;
        }

        var (gitPull, _, gitPullErr) = await _commandExecutor.ExecuteAsync("git", ["pull"], root);
        if (gitPull != 0)
        {
            if (!string.IsNullOrWhiteSpace(gitPullErr)) _err.WriteLine(gitPullErr);
            _err.WriteLine("Git pull failed. Aborting update.");
            return gitPull;
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
            return Path.GetFullPath(explicitRoot);

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Mohist.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        return Directory.GetCurrentDirectory();
    }
}
