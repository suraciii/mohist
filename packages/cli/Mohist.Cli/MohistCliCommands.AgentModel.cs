using System.CommandLine;

namespace Mohist.Cli;

internal static class AgentModelCommands
{
    public static Command Build(MohistCliApi api)
    {
        var model = new Command("model", "Manage the model catalog for an Agent runtime");
        model.Subcommands.Add(BuildList(api));
        return model;
    }

    private static Command BuildList(MohistCliApi api)
    {
        var cmd = new Command(
            "list",
            "List available coder model IDs for the runtime (one per line; use with --model).");

        var runtimeOpt = new Option<string?>("--runtime") { Description = "Filter by runtime (default: project's configured runtime)" };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption(ResourceOutputCatalog.For(nameof(MohistCliApi.TableShape.OpencodeModels)));

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
