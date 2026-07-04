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
                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);
                if (resolveExit != 0)
                {
                    api.Error.WriteLine("No project resolved. " + MohistCliCommands.NoActiveProjectMessage);
                    return resolveExit;
                }

                var (mode, exit) = api.ResolveOutputMode(output);

                if (exit != 0) return exit;

                return await api.PrintOpencodeModelsAsync(resolvedProjectId, mode);
            }
        });

        return cmd;
    }
}
