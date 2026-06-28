using System.CommandLine;
using System.CommandLine.Parsing;

namespace Mohist.Cli;

internal static class OpencodeCommands
{
    public static Command Build(MohistCliApi api)
    {
        var opencode = new Command("opencode", "Opencode runtime utilities");

        opencode.Subcommands.Add(BuildModels(api));

        return opencode;
    }

    private static Command BuildModels(MohistCliApi api)
    {
        var cmd = new Command(
            "models",
            "List available coder model IDs (one per line; copy-paste into --model). Uses GET /api/projects/{projectId}/opencode/models.");

        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption(defaultValue: "table");

        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);

        cmd.SetAction(ctx =>
        {
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);

            return ModelsAsync();

            async Task<int> ModelsAsync()
            {
                var resolvedProjectId = await api.ResolveProjectIdAsync(project, projectId);
                if (resolvedProjectId is null)
                {
                    api.Error.WriteLine("No project resolved. " + MohistCliCommands.NoActiveProjectMessage);
                    return 1;
                }

                var validation = MohistCliApi.ValidateOutputMode(output);
                if (validation is MohistCliApi.OutputModeResult.Invalid invalid)
                {
                    api.Error.WriteLine(invalid.Message);
                    return 1;
                }
                var mode = ((MohistCliApi.OutputModeResult.Valid)validation).Mode;

                return await api.PrintOpencodeModelsAsync(resolvedProjectId, mode);
            }
        });

        return cmd;
    }
}
