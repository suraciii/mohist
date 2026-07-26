using System.CommandLine;
using System.CommandLine.Parsing;

namespace Mohist.Cli;

internal static class LabelCommands
{
    public static Command Build(MohistCliApi api)
    {
        var label = new Command("label", "Issue label utilities");

        label.Subcommands.Add(BuildList(api));
        label.Subcommands.Add(BuildCreate(api));
        label.Subcommands.Add(BuildEdit(api));
        label.Subcommands.Add(BuildDelete(api));

        return label;
    }

    private static Command BuildList(MohistCliApi api)
    {
        var cmd = new Command("list", "List the label catalog for the project");
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption(ResourceOutputCatalog.For(nameof(MohistCliApi.TableShape.LabelList)));
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
                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);

                if (resolveExit != 0) return resolveExit;
                var path = $"/api/projects/{MohistCliCommands.Escape(resolvedProjectId)}/labels/catalog";
                var (mode, exit) = api.ResolveOutputMode(output);

                if (exit != 0) return exit;

                return await api.PrintWithOutputAsync(path, mode, nameof(MohistCliApi.TableShape.LabelList));
            }
        });
        return cmd;
    }

    private static Command BuildCreate(MohistCliApi api)
    {
        var cmd = new Command("create", "Create a label definition in the project catalog");
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

                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);


                if (resolveExit != 0) return resolveExit;

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

    private static Command BuildEdit(MohistCliApi api)
    {
        var cmd = new Command("edit", "Edit a label definition in the project catalog");
        var keyArg = new Argument<string>("key") { Description = "Label key (lowercase, dashes allowed)" };
        var descriptionOpt = new Option<string?>("--description") { Description = "New description of when to use this label" };
        var supportedValuesOpt = new Option<string?>("--supported-values")
            { Description = "Comma-separated list of recommended values (omit to keep current)" };
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
            return UpdateAsync();

            async Task<int> UpdateAsync()
            {
                var keyError = LabelDelta.ValidateKey(key!);
                if (keyError is not null)
                {
                    api.Error.WriteLine(keyError);
                    return 1;
                }

                var hasDescription = description is not null;
                var hasSupportedValues = !string.IsNullOrWhiteSpace(supportedValuesStr);

                if (!hasDescription && !hasSupportedValues)
                {
                    api.Error.WriteLine("At least one of --description or --supported-values must be provided.");
                    return 1;
                }

                if (hasDescription && string.IsNullOrWhiteSpace(description))
                {
                    api.Error.WriteLine("--description must be a non-empty, non-whitespace string.");
                    return 1;
                }

                List<string>? supportedValues = null;
                if (hasSupportedValues)
                {
                    supportedValues = supportedValuesStr!.Split(',')
                        .Select(v => v.Trim())
                        .ToList();
                    if (supportedValues.Any(v => v.Length == 0))
                    {
                        api.Error.WriteLine("Each supported value must be a non-empty, non-whitespace string.");
                        return 1;
                    }
                }

                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);


                if (resolveExit != 0) return resolveExit;

                var path = $"/api/projects/{MohistCliCommands.Escape(resolvedProjectId)}/labels/catalog/{MohistCliCommands.Escape(key!)}";

                var body = new Dictionary<string, object?>();
                if (hasDescription)
                    body["description"] = description;
                if (supportedValues is not null)
                    body["supportedValues"] = supportedValues;

                return await api.PrintPatchAsync(path, body);
            }
        });

        return cmd;
    }

    private static Command BuildDelete(MohistCliApi api)
    {
        var cmd = new Command("delete", "Delete a label definition from the project catalog");
        var keyArg = new Argument<string>("key") { Description = "Label key to delete" };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();

        cmd.Arguments.Add(keyArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);

        cmd.SetAction(ctx =>
        {
            var key = ctx.GetValue(keyArg);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            return DeleteAsync();

            async Task<int> DeleteAsync()
            {
                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);

                if (resolveExit != 0) return resolveExit;

                var path = $"/api/projects/{MohistCliCommands.Escape(resolvedProjectId)}/labels/catalog/{MohistCliCommands.Escape(key!)}";
                return await api.PrintDeleteAsync(path);
            }
        });

        return cmd;
    }
}
