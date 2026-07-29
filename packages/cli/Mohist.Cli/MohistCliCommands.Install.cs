using System.CommandLine;
using System.CommandLine.Parsing;
using Microsoft.Extensions.DependencyInjection;

namespace Mohist.Cli;

internal static class InstallCommands
{
    public static Command Build(IServiceProvider provider)
    {
        var install = new Command("install", "Install mohist components from source");
        var installer = provider.GetRequiredService<IServiceInstaller>();

        install.Subcommands.Add(BuildServerInstall(installer));
        install.Subcommands.Add(BuildRunnerInstall(installer));
        install.Subcommands.Add(BuildSlackInstall(installer));

        return install;
    }

    private static Command BuildServerInstall(IServiceInstaller installer)
    {
        var cmd = new Command("server", "Install server as a managed background service from source");
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
            return installer.InstallServerAsync(new ServiceInstallOptions(dryRun, unitDir, repoRoot, listenUrl, null, null));
        });
        return cmd;
    }

    private static Command BuildRunnerInstall(IServiceInstaller installer)
    {
        var cmd = new Command("runner", "Install runner as a managed background service from source");
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
            return installer.InstallRunnerAsync(new ServiceInstallOptions(dryRun, unitDir, repoRoot, null, serverUrl, runnerRoot));
        });
        return cmd;
    }

    private static Command BuildSlackInstall(IServiceInstaller installer)
    {
        var cmd = new Command("slack", "Install the mohist-slack adapter as a managed background service");
        var repoRootOpt = new Option<string?>("--repo-root") { Description = "Repository root path" };
        var serverUrlOpt = new Option<string?>("--server-url") { Description = "Server URL" };
        var dryRunOpt = MohistCliCommands.DryRunOption();
        var unitDirOpt = MohistCliCommands.UnitDirOption();
        cmd.Options.Add(repoRootOpt);
        cmd.Options.Add(serverUrlOpt);
        cmd.Options.Add(unitDirOpt);
        cmd.Options.Add(dryRunOpt);
        cmd.SetAction(ctx => installer.InstallSlackAsync(new ServiceInstallOptions(
            ctx.GetValue(dryRunOpt), ctx.GetValue(unitDirOpt), ctx.GetValue(repoRootOpt), null,
            ctx.GetValue(serverUrlOpt), null)));
        return cmd;
    }
}
