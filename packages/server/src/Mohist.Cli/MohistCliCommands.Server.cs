using System.CommandLine;
using System.CommandLine.Parsing;
using Microsoft.Extensions.DependencyInjection;

namespace Mohist.Cli;

internal static class ServerCommands
{
    public static Command Build(MohistCliApi api, IServiceProvider provider)
    {
        var server = new Command("server", "Server management");
        var systemd = provider.GetRequiredService<SystemdServiceInstaller>();

        server.Subcommands.Add(BuildHealth(api));
        server.Subcommands.Add(BuildInstall(systemd));
        server.Subcommands.Add(BuildSystemd("start", systemd.StartServerAsync, systemd));
        server.Subcommands.Add(BuildSystemd("stop", systemd.StopServerAsync, systemd));
        server.Subcommands.Add(BuildSystemd("restart", systemd.RestartServerAsync, systemd));
        server.Subcommands.Add(BuildSystemd("status", systemd.StatusServerAsync, systemd));
        server.Subcommands.Add(BuildLogs(systemd));
        server.Subcommands.Add(BuildSystemd("uninstall", systemd.UninstallServerAsync, systemd));

        return server;
    }

    private static Command BuildHealth(MohistCliApi api)
    {
        var cmd = new Command("health", "Check server health");
        cmd.SetAction((ParseResult _) => api.PrintGetAsync("/api/health"));
        return cmd;
    }

    private static Command BuildInstall(SystemdServiceInstaller systemd)
    {
        var cmd = new Command("install", "Install server systemd service");
        var repoRootOpt = new Option<string?>("--repo-root") { Description = "Repository root path" };
        var listenUrlOpt = new Option<string?>("--listen-url") { Description = "Server listen URL" };
        var dryRunOpt = MohistCliCommands.DryRunOption();
        var unitDirOpt = MohistCliCommands.UnitDirOption();
        cmd.Options.Add(repoRootOpt);
        cmd.Options.Add(unitDirOpt);
        cmd.Options.Add(listenUrlOpt);
        cmd.Options.Add(dryRunOpt);
        cmd.SetAction(ctx =>
        {
            var dryRun = ctx.GetValue(dryRunOpt);
            var unitDir = ctx.GetValue(unitDirOpt);
            var repoRoot = ctx.GetValue(repoRootOpt);
            var listenUrl = ctx.GetValue(listenUrlOpt);
            return systemd.InstallServerAsync(new ServiceInstallOptions(dryRun, unitDir, repoRoot, listenUrl, null, null));
        });
        return cmd;
    }

    private static Command BuildSystemd(string name, Func<ServiceCommandOptions, Task<int>> handler, SystemdServiceInstaller systemd)
    {
        var cmd = new Command(name, $"{name} server systemd service");
        var dryRunOpt = MohistCliCommands.DryRunOption();
        var unitDirOpt = MohistCliCommands.UnitDirOption();
        cmd.Options.Add(dryRunOpt);
        cmd.Options.Add(unitDirOpt);
        cmd.SetAction(ctx =>
        {
            var dryRun = ctx.GetValue(dryRunOpt);
            var unitDir = ctx.GetValue(unitDirOpt);
            return handler(new ServiceCommandOptions(dryRun, unitDir, 100, false));
        });
        return cmd;
    }

    private static Command BuildLogs(SystemdServiceInstaller systemd)
    {
        var cmd = new Command("logs", "View server service logs");
        var linesOpt = MohistCliCommands.LinesOption();
        var followOpt = MohistCliCommands.FollowOption();
        var dryRunOpt = MohistCliCommands.DryRunOption();
        var unitDirOpt = MohistCliCommands.UnitDirOption();
        cmd.Options.Add(linesOpt);
        cmd.Options.Add(followOpt);
        cmd.Options.Add(dryRunOpt);
        cmd.Options.Add(unitDirOpt);
        cmd.SetAction(ctx =>
        {
            var lines = ctx.GetValue(linesOpt);
            var follow = ctx.GetValue(followOpt);
            var dryRun = ctx.GetValue(dryRunOpt);
            var unitDir = ctx.GetValue(unitDirOpt);
            return systemd.LogsServerAsync(new ServiceCommandOptions(dryRun, unitDir, lines, follow));
        });
        return cmd;
    }
}

internal static class RunnerCommands
{
    public static Command Build(MohistCliApi api, IServiceProvider provider)
    {
        var runner = new Command("runner", "Runner management");
        var systemd = provider.GetRequiredService<SystemdServiceInstaller>();

        runner.Subcommands.Add(BuildInstall(systemd));
        runner.Subcommands.Add(BuildSystemd("start", systemd.StartRunnerAsync, systemd));
        runner.Subcommands.Add(BuildSystemd("stop", systemd.StopRunnerAsync, systemd));
        runner.Subcommands.Add(BuildSystemd("restart", systemd.RestartRunnerAsync, systemd));
        runner.Subcommands.Add(BuildSystemd("status", systemd.StatusRunnerAsync, systemd));
        runner.Subcommands.Add(BuildLogs(systemd));
        runner.Subcommands.Add(BuildSystemd("uninstall", systemd.UninstallRunnerAsync, systemd));

        return runner;
    }

    private static Command BuildInstall(SystemdServiceInstaller systemd)
    {
        var cmd = new Command("install", "Install runner systemd service");
        var repoRootOpt = new Option<string?>("--repo-root") { Description = "Repository root path" };
        var serverUrlOpt = new Option<string?>("--server-url") { Description = "Server URL" };
        var runnerRootOpt = new Option<string?>("--runner-root") { Description = "Runner root path" };
        var dryRunOpt = MohistCliCommands.DryRunOption();
        var unitDirOpt = MohistCliCommands.UnitDirOption();
        cmd.Options.Add(repoRootOpt);
        cmd.Options.Add(unitDirOpt);
        cmd.Options.Add(serverUrlOpt);
        cmd.Options.Add(runnerRootOpt);
        cmd.Options.Add(dryRunOpt);
        cmd.SetAction(ctx =>
        {
            var dryRun = ctx.GetValue(dryRunOpt);
            var unitDir = ctx.GetValue(unitDirOpt);
            var repoRoot = ctx.GetValue(repoRootOpt);
            var serverUrl = ctx.GetValue(serverUrlOpt);
            var runnerRoot = ctx.GetValue(runnerRootOpt);
            return systemd.InstallRunnerAsync(new ServiceInstallOptions(dryRun, unitDir, repoRoot, null, serverUrl, runnerRoot));
        });
        return cmd;
    }

    private static Command BuildSystemd(string name, Func<ServiceCommandOptions, Task<int>> handler, SystemdServiceInstaller systemd)
    {
        var cmd = new Command(name, $"{name} runner systemd service");
        var dryRunOpt = MohistCliCommands.DryRunOption();
        var unitDirOpt = MohistCliCommands.UnitDirOption();
        cmd.Options.Add(dryRunOpt);
        cmd.Options.Add(unitDirOpt);
        cmd.SetAction(ctx =>
        {
            var dryRun = ctx.GetValue(dryRunOpt);
            var unitDir = ctx.GetValue(unitDirOpt);
            return handler(new ServiceCommandOptions(dryRun, unitDir, 100, false));
        });
        return cmd;
    }

    private static Command BuildLogs(SystemdServiceInstaller systemd)
    {
        var cmd = new Command("logs", "View runner service logs");
        var linesOpt = MohistCliCommands.LinesOption();
        var followOpt = MohistCliCommands.FollowOption();
        var dryRunOpt = MohistCliCommands.DryRunOption();
        var unitDirOpt = MohistCliCommands.UnitDirOption();
        cmd.Options.Add(linesOpt);
        cmd.Options.Add(followOpt);
        cmd.Options.Add(dryRunOpt);
        cmd.Options.Add(unitDirOpt);
        cmd.SetAction(ctx =>
        {
            var lines = ctx.GetValue(linesOpt);
            var follow = ctx.GetValue(followOpt);
            var dryRun = ctx.GetValue(dryRunOpt);
            var unitDir = ctx.GetValue(unitDirOpt);
            return systemd.LogsRunnerAsync(new ServiceCommandOptions(dryRun, unitDir, lines, follow));
        });
        return cmd;
    }
}
