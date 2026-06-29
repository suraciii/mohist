using System.CommandLine;

namespace Mohist.Cli;

internal static class ProjectWorkflowCommands
{
    public static Command Build(MohistCliApi api)
    {
        var workflow = new Command("workflow", "Project workflow management");

        workflow.Subcommands.Add(BuildTemplate(api));
        workflow.Subcommands.Add(BuildConfig(api));

        return workflow;
    }

    private static Command BuildConfig(MohistCliApi api)
    {
        var config = new Command("config", "Manage project workflow configuration");
        return config;
    }

    private static Command BuildTemplate(MohistCliApi api)
    {
        var template = new Command("template", "Manage project workflow templates");

        template.Subcommands.Add(BuildTemplateList(api));
        template.Subcommands.Add(BuildTemplateCreate(api));
        template.Subcommands.Add(BuildTemplateShow(api));
        template.Subcommands.Add(BuildTemplateUpdate(api));
        template.Subcommands.Add(BuildTemplateDelete(api));

        return template;
    }

    private static Command BuildTemplateList(MohistCliApi api)
    {
        var cmd = new Command("list", "List workflow templates");
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
            var validation = MohistCliApi.ValidateOutputMode(output);
            if (validation is MohistCliApi.OutputModeResult.Invalid invalid)
            {
                api.Error.WriteLine(invalid.Message);
                return Task.FromResult(1);
            }
            var mode = ((MohistCliApi.OutputModeResult.Valid)validation).Mode;
            return ListAsync();

            async Task<int> ListAsync()
            {
                var resolvedProjectId = await api.ResolveProjectIdAsync(project, projectId);
                if (string.IsNullOrWhiteSpace(resolvedProjectId))
                    return 1;
                return await api.PrintWithOutputAsync(
                    $"/api/projects/{MohistCliCommands.Escape(resolvedProjectId)}/workflow-templates",
                    mode,
                    nameof(MohistCliApi.TableShape.ProjectTemplateList));
            }
        });
        return cmd;
    }

    private static Command BuildTemplateCreate(MohistCliApi api)
    {
        var cmd = new Command("create", "Create a workflow template");
        var yamlOpt = new Option<string>("--yaml") { Description = "Template YAML body (inline or @file)" };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Options.Add(yamlOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var yaml = ctx.GetValue(yamlOpt);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            var validation = MohistCliApi.ValidateOutputMode(output);
            if (validation is MohistCliApi.OutputModeResult.Invalid invalid)
            {
                api.Error.WriteLine(invalid.Message);
                return Task.FromResult(1);
            }
            var mode = ((MohistCliApi.OutputModeResult.Valid)validation).Mode;
            return CreateAsync();

            async Task<int> CreateAsync()
            {
                var expanded = await api.ExpandAtFileAsync(yaml, "--yaml");
                if (expanded is MohistCliApi.ExpandAtFileResult.Failure)
                    return 1;
                var body = ((MohistCliApi.ExpandAtFileResult.Success)expanded).Value;
                var resolvedProjectId = await api.ResolveProjectIdAsync(project, projectId);
                if (string.IsNullOrWhiteSpace(resolvedProjectId))
                    return 1;
                return await api.PrintPostWithOutputAsync(
                    $"/api/projects/{MohistCliCommands.Escape(resolvedProjectId)}/workflow-templates",
                    new { yaml = body },
                    mode,
                    nameof(MohistCliApi.TableShape.ProjectTemplateShow));
            }
        });
        return cmd;
    }

    private static Command BuildTemplateShow(MohistCliApi api)
    {
        var cmd = new Command("show", "Show a workflow template");
        var templateIdArg = new Argument<string>("template-id") { Description = "Template ID" };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Arguments.Add(templateIdArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var templateId = ctx.GetValue(templateIdArg);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            var validation = MohistCliApi.ValidateOutputMode(output);
            if (validation is MohistCliApi.OutputModeResult.Invalid invalid)
            {
                api.Error.WriteLine(invalid.Message);
                return Task.FromResult(1);
            }
            var mode = ((MohistCliApi.OutputModeResult.Valid)validation).Mode;
            return ShowAsync();

            async Task<int> ShowAsync()
            {
                var resolvedProjectId = await api.ResolveProjectIdAsync(project, projectId);
                if (string.IsNullOrWhiteSpace(resolvedProjectId))
                    return 1;
                return await api.PrintWithOutputAsync(
                    $"/api/projects/{MohistCliCommands.Escape(resolvedProjectId)}/workflow-templates/{MohistCliCommands.Escape(templateId!)}",
                    mode,
                    nameof(MohistCliApi.TableShape.ProjectTemplateShow));
            }
        });
        return cmd;
    }

    private static Command BuildTemplateUpdate(MohistCliApi api)
    {
        var cmd = new Command("update", "Update a workflow template");
        var templateIdArg = new Argument<string>("template-id") { Description = "Template ID" };
        var yamlOpt = new Option<string>("--yaml") { Description = "Template YAML body (inline or @file)" };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Arguments.Add(templateIdArg);
        cmd.Options.Add(yamlOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var templateId = ctx.GetValue(templateIdArg);
            var yaml = ctx.GetValue(yamlOpt);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            var validation = MohistCliApi.ValidateOutputMode(output);
            if (validation is MohistCliApi.OutputModeResult.Invalid invalid)
            {
                api.Error.WriteLine(invalid.Message);
                return Task.FromResult(1);
            }
            var mode = ((MohistCliApi.OutputModeResult.Valid)validation).Mode;
            return UpdateAsync();

            async Task<int> UpdateAsync()
            {
                var expanded = await api.ExpandAtFileAsync(yaml, "--yaml");
                if (expanded is MohistCliApi.ExpandAtFileResult.Failure)
                    return 1;
                var body = ((MohistCliApi.ExpandAtFileResult.Success)expanded).Value;
                var resolvedProjectId = await api.ResolveProjectIdAsync(project, projectId);
                if (string.IsNullOrWhiteSpace(resolvedProjectId))
                    return 1;
                return await api.PrintPutWithOutputAsync(
                    $"/api/projects/{MohistCliCommands.Escape(resolvedProjectId)}/workflow-templates/{MohistCliCommands.Escape(templateId!)}",
                    new { yaml = body },
                    mode,
                    nameof(MohistCliApi.TableShape.ProjectTemplateShow));
            }
        });
        return cmd;
    }

    private static Command BuildTemplateDelete(MohistCliApi api)
    {
        var cmd = new Command("delete", "Delete a workflow template");
        cmd.Aliases.Add("remove");
        cmd.Aliases.Add("rm");
        var templateIdArg = new Argument<string>("template-id") { Description = "Template ID" };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Arguments.Add(templateIdArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var templateId = ctx.GetValue(templateIdArg);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            var validation = MohistCliApi.ValidateOutputMode(output);
            if (validation is MohistCliApi.OutputModeResult.Invalid invalid)
            {
                api.Error.WriteLine(invalid.Message);
                return Task.FromResult(1);
            }
            var mode = ((MohistCliApi.OutputModeResult.Valid)validation).Mode;
            return DeleteAsync();

            async Task<int> DeleteAsync()
            {
                var resolvedProjectId = await api.ResolveProjectIdAsync(project, projectId);
                if (string.IsNullOrWhiteSpace(resolvedProjectId))
                    return 1;
                return await api.PrintDeleteWithOutputAsync(
                    $"/api/projects/{MohistCliCommands.Escape(resolvedProjectId)}/workflow-templates/{MohistCliCommands.Escape(templateId!)}",
                    mode,
                    nameof(MohistCliApi.TableShape.ProjectTemplateShow));
            }
        });
        return cmd;
    }
}
