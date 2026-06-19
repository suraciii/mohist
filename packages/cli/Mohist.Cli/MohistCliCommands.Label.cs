using System.CommandLine;
using System.CommandLine.Parsing;

namespace Mohist.Cli;

internal static class LabelCommands
{
    public static Command Build(MohistCliApi api)
    {
        var label = new Command("label", "Issue label utilities");

        label.Subcommands.Add(BuildList(api));

        return label;
    }

    private static Command BuildList(MohistCliApi api)
    {
        var cmd = new Command("list", "List the distinct label keys used in the project");
        cmd.Aliases.Add("ls");
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            return ListAsync();

            async Task<int> ListAsync()
            {
                var resolvedProjectId = await api.ResolveProjectIdAsync(project, projectId);
                if (resolvedProjectId is null)
                    return 1;
                var path = $"/api/projects/{MohistCliCommands.Escape(resolvedProjectId)}/labels";
                var validation = MohistCliApi.ValidateOutputMode(output);
                if (validation is MohistCliApi.OutputModeResult.Invalid invalid)
                {
                    api.Error.WriteLine(invalid.Message);
                    return 1;
                }
                var mode = ((MohistCliApi.OutputModeResult.Valid)validation).Mode;
                if (string.Equals(mode, "table", StringComparison.Ordinal))
                {
                    var data = await api.GetDataSafeAsync(path);
                    if (data is null) return 1;
                    RenderLabelKeysTable(api.Output, data);
                    return 0;
                }
                return await api.PrintGetAsync(path);
            }
        });
        return cmd;
    }

    internal static void RenderLabelKeysTable(TextWriter output, System.Text.Json.Nodes.JsonNode? data)
    {
        if (data is not System.Text.Json.Nodes.JsonArray array || array.Count == 0)
        {
            output.WriteLine("No labels");
            return;
        }
        output.WriteLine("label key");
        foreach (var node in array)
        {
            if (node is null) continue;
            var key = node.GetValue<string>();
            if (!string.IsNullOrEmpty(key))
                output.WriteLine(key);
        }
    }
}
