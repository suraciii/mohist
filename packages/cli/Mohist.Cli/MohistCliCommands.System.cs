using System.CommandLine;
using System.CommandLine.Parsing;

namespace Mohist.Cli;

internal static class SystemCommands
{
    public static Command Build(MohistCliApi api)
    {
        var system = new Command(
            "system",
            "Application diagnostics. 'mo system logs' reports application logs (the Mohist server's own log tail), distinct from 'mo server logs' which reports operational/service-manager logs (systemd journal or scheduled-task output). Distinct from 'mo info', which reports the CLI binary's own local environment and install source.");

        system.Subcommands.Add(BuildLogs(api));

        return system;
    }

    private static Command BuildLogs(MohistCliApi api)
    {
        var cmd = new Command("logs", "Show recent application logs (Mohist server's own log tail). For operational/service-manager logs (systemd journal), use 'mo server logs'.");
        cmd.SetAction((ParseResult _) => api.PrintGetAsync("/api/logs/tail"));
        return cmd;
    }
}
