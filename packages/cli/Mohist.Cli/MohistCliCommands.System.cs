using System.CommandLine;
using System.CommandLine.Parsing;

namespace Mohist.Cli;

internal static class SystemCommands
{
    public static Command Build(MohistCliApi api)
    {
        var system = new Command(
            "system",
            "Server-side system diagnostics. Distinct from 'mo info', which reports the CLI binary's own local environment and install source.");

        system.Subcommands.Add(BuildInfo(api));

        return system;
    }

    private static Command BuildInfo(MohistCliApi api)
    {
        var cmd = new Command(
            "info",
            "Show server-side system diagnostics (identity, source, install, update, services, paths). Distinct from 'mo info' (CLI-local environment).");

        var outputOpt = MohistCliCommands.OutputOption(defaultValue: "table");

        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var output = ctx.GetValue(outputOpt);
            var validation = MohistCliApi.ValidateOutputMode(output);
            if (validation is MohistCliApi.OutputModeResult.Invalid invalid)
            {
                api.Error.WriteLine(invalid.Message);
                return Task.FromResult(1);
            }

            var mode = ((MohistCliApi.OutputModeResult.Valid)validation).Mode;
            return api.PrintSystemInfoAsync(mode);
        });

        return cmd;
    }
}
