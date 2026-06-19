using System.CommandLine;
using System.CommandLine.Parsing;

namespace Mohist.Cli;

internal static class LabelCommands
{
    public static Command Build(MohistCliApi api)
    {
        var label = new Command("label", "Issue label utilities");

        label.Subcommands.Add(BuildList(api));
        label.Subcommands.Add(BuildAdd(api));
        label.Subcommands.Add(BuildRemove(api));

        return label;
    }

    private static Command BuildList(MohistCliApi api)
    {
        var cmd = new Command("list", "List the label catalog for the project");
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
                var path = $"/api/projects/{MohistCliCommands.Escape(resolvedProjectId)}/labels/catalog";
                var validation = MohistCliApi.ValidateOutputMode(output);
                if (validation is MohistCliApi.OutputModeResult.Invalid invalid)
                {
                    api.Error.WriteLine(invalid.Message);
                    return 1;
                }
                var mode = ((MohistCliApi.OutputModeResult.Valid)validation).Mode;
                return await api.PrintWithOutputAsync(path, mode, nameof(MohistCliApi.TableShape.LabelList));
            }
        });
        return cmd;
    }

    private static Command BuildAdd(MohistCliApi api)
    {
        var cmd = new Command("add", "Add a label definition to the project catalog");
        var keyArg = new Argument<string>("key") { Description = "Label key (lowercase, dashes allowed)" };
        var descriptionOpt = new Option<string>("--description") { Description = "Description of when to use this label" };
        var supportedValuesOpt = new Option<string?>("--supported-values")
            { Description = "Comma-separated list of recommended values" };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();

        cmd.Arguments.Add(keyArg);
        cmd.Options.Add(descriptionOpt);
        cmd.Options.Add(supportedValuesOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);

        cmd.SetAction(ctx =>
        {
            var key = ctx.GetValue(keyArg);
            var description = ctx.GetValue(descriptionOpt);
            var supportedValuesStr = ctx.GetValue(supportedValuesOpt);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            return AddAsync();

            async Task<int> AddAsync()
            {
                var keyError = LabelDelta.ValidateKey(key!);
                if (keyError is not null)
                {
                    api.Error.WriteLine(keyError);
                    return 1;
                }

                if (string.IsNullOrWhiteSpace(description))
                {
                    api.Error.WriteLine("--description is required and must not be empty");
                    return 1;
                }

                var resolvedProjectId = await api.ResolveProjectIdAsync(project, projectId);
                if (resolvedProjectId is null)
                    return 1;

                var path = $"/api/projects/{MohistCliCommands.Escape(resolvedProjectId)}/labels/catalog";

                List<string>? supportedValues = null;
                if (!string.IsNullOrWhiteSpace(supportedValuesStr))
                {
                    supportedValues = supportedValuesStr.Split(',')
                        .Select(v => v.Trim())
                        .ToList();
                    if (supportedValues.Any(v => v.Length == 0))
                    {
                        api.Error.WriteLine("Each supported value must be a non-empty, non-whitespace string.");
                        return 1;
                    }
                }

                return await api.PrintPostAsync(path, new
                {
                    key,
                    description,
                    supportedValues,
                });
            }
        });

        return cmd;
    }

    private static Command BuildRemove(MohistCliApi api)
    {
        var cmd = new Command("remove", "Remove a label definition from the project catalog");
        cmd.Aliases.Add("rm");
        var keyArg = new Argument<string>("key") { Description = "Label key to remove" };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();

        cmd.Arguments.Add(keyArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);

        cmd.SetAction(ctx =>
        {
            var key = ctx.GetValue(keyArg);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            return RemoveAsync();

            async Task<int> RemoveAsync()
            {
                var resolvedProjectId = await api.ResolveProjectIdAsync(project, projectId);
                if (resolvedProjectId is null)
                    return 1;

                var path = $"/api/projects/{MohistCliCommands.Escape(resolvedProjectId)}/labels/catalog/{MohistCliCommands.Escape(key!)}";
                return await api.PrintDeleteAsync(path);
            }
        });

        return cmd;
    }
}
