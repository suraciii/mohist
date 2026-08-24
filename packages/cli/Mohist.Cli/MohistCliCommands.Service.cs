using System.CommandLine;
using System.CommandLine.Parsing;
using Microsoft.Extensions.DependencyInjection;

namespace Mohist.Cli;

internal enum ServiceTarget
{
    Server,
    Runner,
    Slack,
}

internal static class ServiceCommands
{
    private const string TargetDescription = "Target managed service: 'server', 'runner', or 'slack'";

    private static readonly Dictionary<(string Verb, ServiceTarget Target), Func<IServiceInstaller, ServiceCommandOptions, CancellationToken, Task<int>>> Dispatch = new()
    {
        [("start", ServiceTarget.Server)] = (i, o, _) => i.StartServerAsync(o),
        [("stop", ServiceTarget.Server)] = (i, o, _) => i.StopServerAsync(o),
        [("restart", ServiceTarget.Server)] = (i, o, _) => i.RestartServerAsync(o),
        [("status", ServiceTarget.Server)] = (i, o, _) => i.StatusServerAsync(o),
        [("logs", ServiceTarget.Server)] = (i, o, _) => i.LogsServerAsync(o),
        [("uninstall", ServiceTarget.Server)] = (i, o, _) => i.UninstallServerAsync(o),
        [("start", ServiceTarget.Runner)] = (i, o, _) => i.StartRunnerAsync(o),
        [("stop", ServiceTarget.Runner)] = (i, o, _) => i.StopRunnerAsync(o),
        [("restart", ServiceTarget.Runner)] = (i, o, _) => i.RestartRunnerAsync(o),
        [("status", ServiceTarget.Runner)] = (i, o, _) => i.StatusRunnerAsync(o),
        [("logs", ServiceTarget.Runner)] = (i, o, _) => i.LogsRunnerAsync(o),
        [("uninstall", ServiceTarget.Runner)] = (i, o, _) => i.UninstallRunnerAsync(o),
        [("start", ServiceTarget.Slack)] = (i, o, token) => i.StartSlackAsync(o, token),
        [("stop", ServiceTarget.Slack)] = (i, o, token) => i.StopSlackAsync(o, token),
        [("restart", ServiceTarget.Slack)] = (i, o, token) => i.RestartSlackAsync(o, token),
        [("status", ServiceTarget.Slack)] = (i, o, _) => i.StatusSlackAsync(o),
        [("logs", ServiceTarget.Slack)] = (i, o, _) => i.LogsSlackAsync(o),
        [("uninstall", ServiceTarget.Slack)] = (i, o, _) => i.UninstallSlackAsync(o),
    };

    public static Command Build(IServiceProvider provider)
    {
        var service = new Command(
            "service",
            "Local managed-process lifecycle (systemd unit on Linux, scheduled task on Windows). Acts only on the OS-level managed service for the selected target. Use 'mo install <server|runner>' to install and 'mo update <server|runner>' to update; service does not parse Project.");

        var installer = provider.GetRequiredService<IServiceInstaller>();

        service.Subcommands.Add(BuildSimple("start", "start the local managed service for the given <target>", installer));
        service.Subcommands.Add(BuildSimple("stop", "stop the local managed service for the given <target>", installer));
        service.Subcommands.Add(BuildSimple("restart", "restart the local managed service for the given <target>", installer));
        service.Subcommands.Add(BuildSimple("status", "Show lifecycle status of the local managed service for <target>", installer));
        service.Subcommands.Add(BuildLogs(installer));
        service.Subcommands.Add(BuildSimple("uninstall", "uninstall the local managed service for the given <target>", installer));

        return service;
    }

    private static Command BuildSimple(string verb, string description, IServiceInstaller installer)
    {
        var targetArg = BuildTargetArgument(verb);
        var dryRunOpt = MohistCliCommands.DryRunOption();
        var unitDirOpt = MohistCliCommands.UnitDirOption();
        var cmd = new Command(verb, description);
        cmd.Arguments.Add(targetArg);
        cmd.Options.Add(dryRunOpt);
        cmd.Options.Add(unitDirOpt);
        cmd.SetAction(async (ctx, cancellationToken) =>
        {
            var target = ctx.GetValue(targetArg);
            var dryRun = ctx.GetValue(dryRunOpt);
            var unitDir = ctx.GetValue(unitDirOpt);
            var options = new ServiceCommandOptions(dryRun, unitDir, Lines: 100, Follow: false);
            try
            {
                return await Dispatch[(verb, target)](installer, options, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return CliExitCode.For(CliExitOutcome.Cancelled);
            }
        });
        return cmd;
    }

    private static Command BuildLogs(IServiceInstaller installer)
    {
        var verb = "logs";
        var targetArg = BuildTargetArgument(verb);
        var linesOpt = MohistCliCommands.LinesOption();
        var followOpt = MohistCliCommands.FollowOption();
        var dryRunOpt = MohistCliCommands.DryRunOption();
        var unitDirOpt = MohistCliCommands.UnitDirOption();
        var cmd = new Command(
            verb,
            "Tail the service-manager logs (systemd journal or scheduled-task output) for the given <target>. These are service-manager logs and are not interchangeable with application logs; use 'mo server logs' to read the connected application's own log tail.");
        cmd.Arguments.Add(targetArg);
        cmd.Options.Add(linesOpt);
        cmd.Options.Add(followOpt);
        cmd.Options.Add(dryRunOpt);
        cmd.Options.Add(unitDirOpt);
        cmd.SetAction(async (ctx, cancellationToken) =>
        {
            var target = ctx.GetValue(targetArg);
            var lines = ctx.GetValue(linesOpt);
            var follow = ctx.GetValue(followOpt);
            var dryRun = ctx.GetValue(dryRunOpt);
            var unitDir = ctx.GetValue(unitDirOpt);
            var options = new ServiceCommandOptions(dryRun, unitDir, lines, follow);
            try
            {
                return await Dispatch[(verb, target)](installer, options, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return CliExitCode.For(CliExitOutcome.Cancelled);
            }
        });
        return cmd;
    }

    private static Argument<ServiceTarget> BuildTargetArgument(string verb)
    {
        var arg = new Argument<ServiceTarget>("target")
        {
            Arity = ArgumentArity.ExactlyOne,
        };
        arg.Description = $"{TargetDescription} (required for '{verb}')";
        return arg;
    }
}
