using System.CommandLine;
using System.CommandLine.Parsing;
using Microsoft.Extensions.DependencyInjection;

namespace Mohist.Cli;

internal static class InfoCommands
{
    public static Command Build(IServiceProvider provider)
    {
        var info = new Command("info", "Show environment and installation source overview");
        var verboseOption = new Option<bool>("--verbose", "-v")
        {
            Description = "Append supplementary sections (skills, git remote, opencode, env, OS, capacity, disk)",
        };
        var jsonOption = new Option<bool>("--json")
        {
            Description = "Output a single machine-readable JSON object to stdout",
        };
        info.Options.Add(verboseOption);
        info.Options.Add(jsonOption);
        var collector = provider.GetRequiredService<InfoCollector>();
        var renderer = provider.GetRequiredService<InfoRenderer>();

        info.SetAction(ctx =>
        {
            var verbose = ctx.GetValue(verboseOption);
            var json = ctx.GetValue(jsonOption);
            var result = collector.CollectAsync(verbose).GetAwaiter().GetResult();
            var writer = ctx.InvocationConfiguration.Output;
            if (json)
            {
                renderer.RenderJson(writer, result);
                return 0;
            }
            renderer.RenderDefault(writer, result);
            if (verbose && result.Verbose is not null)
                renderer.RenderVerbose(writer, result.Verbose);
            return 0;
        });

        return info;
    }
}
