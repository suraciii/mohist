using System.CommandLine;
using System.CommandLine.Parsing;
using Microsoft.Extensions.DependencyInjection;

namespace Mohist.Cli;

internal static class InfoCommands
{
    private static readonly ResourceDescriptor Descriptor = new(
        ResourceCardinality.Single,
        [
            "cli",
            "server",
            "runner",
            "project",
            "dataDir",
            "platformNotice",
            "skills",
            "gitRemote",
            "opencodeRuntime",
            "envVars",
            "osRuntime",
            "capacity",
            "diskUsage",
        ]);

    private static readonly HashSet<string> VerboseFields =
    [
        "skills",
        "gitRemote",
        "opencodeRuntime",
        "envVars",
        "osRuntime",
        "capacity",
        "diskUsage",
    ];

    public static Command Build(IServiceProvider provider)
    {
        var info = new Command("info", "Show environment and installation source overview");
        var verboseOption = new Option<bool>("--verbose", "-v")
        {
            Description = "Append supplementary sections (skills, git remote, opencode, env, OS, capacity, disk)",
        };
        var jsonOption = MohistCliCommands.JsonSelectionOption();
        info.Options.Add(verboseOption);
        info.Options.Add(jsonOption);
        var collector = provider.GetService<InfoCollector>();
        var renderer = provider.GetService<InfoRenderer>() ?? new InfoRenderer();
        var api = provider.GetService<MohistCliApi>();

        info.SetAction(ctx =>
        {
            var verbose = ctx.GetValue(verboseOption);
            var selection = JsonSelection.Parse(
                Descriptor,
                ctx.GetResult(jsonOption) is not null,
                ctx.GetValue(jsonOption));
            if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
            {
                if (api is null)
                {
                    ctx.InvocationConfiguration.Error.WriteLine("info command is unavailable: MohistCliApi service is not registered.");
                    return 1;
                }
                return api.WriteJsonSelectionResult(Descriptor, selection);
            }
            if (collector is null)
            {
                ctx.InvocationConfiguration.Error.WriteLine("info command is unavailable: InfoCollector service is not registered.");
                return 1;
            }
            var includeVerbose = verbose
                || selection.Fields.Any(field => VerboseFields.Contains(field));
            var result = collector.CollectAsync(includeVerbose).GetAwaiter().GetResult();
            var writer = ctx.InvocationConfiguration.Output;
            if (selection.Kind == JsonSelectionKind.Selected)
            {
                var projected = selection.Project(
                    InfoRenderer.BuildJsonObject(result),
                    Descriptor.Cardinality);
                writer.WriteLine(projected.ToJsonString(MohistCliApi.JsonOutputOptions));
                return CliExitCode.For(CliExitOutcome.Success);
            }
            renderer.RenderDefault(writer, result);
            if (verbose && result.Verbose is not null)
                renderer.RenderVerbose(writer, result.Verbose);
            return 0;
        });

        return info;
    }
}
