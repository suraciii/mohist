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

        var runtimeOpt = new Option<string?>("--runtime") { Description = "Filter by runtime (default: opencode)" };
        var projectOpt = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption(ResourceOutputCatalog.For(nameof(MohistCliApi.TableShape.Models)));

        cmd.Options.Add(runtimeOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(outputOpt);

        cmd.SetAction(ctx =>
        {
            var runtime = ctx.GetValue(runtimeOpt);
            var project = ctx.GetValue(projectOpt);
            var output = ctx.GetValue(outputOpt);

            return ListAsync();

            async Task<int> ListAsync()
            {
                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project);
                if (resolveExit != 0)
                {
                    api.Error.WriteLine("No project resolved. " + MohistCliCommands.NoActiveProjectMessage);
                    return resolveExit;
                }

                var (mode, exit) = api.ResolveOutputMode(output);

                if (exit != 0) return exit;

                return await api.PrintOpencodeModelsAsync(resolvedProjectId, runtime, mode);
            }
        });

        return cmd;
    }
}
