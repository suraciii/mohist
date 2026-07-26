using System.CommandLine;

namespace Mohist.Cli;

internal static class AgentModelCommands
{
    public static Command Build(MohistCliApi api)
    {
        var model = new Command("model", "Agent model catalog");
        model.Subcommands.Add(BuildList(api));
        return model;
    }

    private static Command BuildList(MohistCliApi api)
    {
        var cmd = new Command(
            "list",
            "List available coder model IDs for the runtime (one per line; copy-paste into --model). Uses GET /api/projects/{projectId}/opencode/models.");

        var runtimeOpt = new Option<string?>("--runtime") { Description = "Filter by runtime (default: project's configured runtime)" };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption(defaultValue: "table");

        cmd.Options.Add(runtimeOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);

        cmd.SetAction(ctx =>
        {
            var runtime = ctx.GetValue(runtimeOpt);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);

            return ListAsync();

            async Task<int> ListAsync()
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