using System.CommandLine;
using System.Net.Http;

namespace Mohist.Cli;

internal static class WorkflowCommands
{
    private static string Path(string projectId, string? profileId = null) =>
        $"/api/projects/{MohistCliCommands.Escape(projectId)}/workflow-profiles" +
        (profileId is null ? string.Empty : $"/{MohistCliCommands.Escape(profileId)}");

    public static Command Build(MohistCliApi api)
    {
        var workflow = new Command("workflow", "Manage Workflow Profiles in the current Project");
        workflow.Subcommands.Add(BuildList(api));
        workflow.Subcommands.Add(BuildView(api));
        workflow.Subcommands.Add(BuildCreate(api));
        workflow.Subcommands.Add(BuildEdit(api));
        workflow.Subcommands.Add(BuildDelete(api));
        workflow.Subcommands.Add(BuildValidate(api));
        return workflow;
    }

    private static (Option<string?> Project, Option<string?> ProjectId) AddProjectOptions(Command command)
    {
        var (project, projectId) = MohistCliCommands.ProjectRefOption();
        command.Options.Add(project);
        command.Options.Add(projectId);
        return (project, projectId);
    }

    private static Command BuildList(MohistCliApi api)
    {
        var cmd = new Command("list", "List Profiles in the current Project");
        var output = MohistCliCommands.OutputOption();
        cmd.Options.Add(output);
        var (project, projectId) = AddProjectOptions(cmd);
        cmd.SetAction(async ctx =>
        {
            var (mode, exit) = api.ResolveOutputMode(ctx.GetValue(output));
            if (exit != 0) return exit;
            var (resolved, resolveExit) = await api.ResolveProject(ctx.GetValue(project), ctx.GetValue(projectId));
            if (resolveExit != 0) return resolveExit;
            return await api.PrintWithOutputAsync(Path(resolved), mode, nameof(MohistCliApi.TableShape.WorkflowProfileList));
        });
        return cmd;
    }

    private static Command BuildView(MohistCliApi api)
    {
        var cmd = new Command("view", "View a Workflow Profile");
        var profile = new Argument<string>("profile") { Description = "Profile ID" };
        var yaml = new Option<bool>("--yaml") { Description = "Print the Profile Definition source" };
        var json = MohistCliCommands.JsonSelectionOption();
        cmd.Arguments.Add(profile);
        cmd.Options.Add(yaml);
        cmd.Options.Add(json);
        var (project, projectId) = AddProjectOptions(cmd);
        cmd.SetAction(async ctx =>
        {
            if (ctx.GetValue(yaml) && ctx.GetResult(json) is not null)
            {
                api.Error.WriteLine("--yaml and --json are mutually exclusive");
                return 2;
            }
            var selection = JsonSelection.Parse(new ResourceDescriptor(ResourceCardinality.Single, ["profileId", "name", "description", "definitionSource", "sourceProvenance", "isBuiltIn"]), ctx.GetResult(json) is not null, ctx.GetValue(json));
            if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
                return api.WriteJsonSelectionResult(new ResourceDescriptor(ResourceCardinality.Single, ["profileId", "name", "description", "definitionSource", "sourceProvenance", "isBuiltIn"]), selection);
            var (resolved, resolveExit) = await api.ResolveProject(ctx.GetValue(project), ctx.GetValue(projectId));
            if (resolveExit != 0) return resolveExit;
            var data = await api.GetDataOrPrintErrorAsync(Path(resolved, ctx.GetValue(profile)));
            if (data.ExitCode != 0 || data.Data is null) return data.ExitCode == 0 ? 1 : data.ExitCode;
            if (ctx.GetValue(yaml))
            {
                api.Output.Write(data.Data["definitionSource"]?.GetValue<string>() ?? string.Empty);
                return 0;
            }
            return ctx.GetResult(json) is null
                ? await api.RenderTableAsync(data.Data, MohistCliApi.TableShape.WorkflowProfile)
                : await api.WriteSelectedDataAsync(data.Data, "json:" + ctx.GetValue(json), nameof(MohistCliApi.TableShape.WorkflowProfile));
        });
        return cmd;
    }

    private static Command BuildCreate(MohistCliApi api) => BuildSave(api, "create", "Create a Workflow Profile", HttpMethod.Post, "profile");

    private static Command BuildEdit(MohistCliApi api) => BuildSave(api, "edit", "Edit a Workflow Profile; changes can affect future stages of active Runs", HttpMethod.Put, "profile");

    private static Command BuildSave(MohistCliApi api, string name, string description, HttpMethod method, string? profileArgument)
    {
        var cmd = new Command(name, description);
        Argument<string?>? profile = profileArgument is null ? null : new Argument<string?>(profileArgument) { Description = "Profile ID", Arity = name == "create" ? ArgumentArity.ZeroOrOne : ArgumentArity.ExactlyOne };
        if (profile is not null) cmd.Arguments.Add(profile);
        var id = new Option<string?>("--id") { Description = "Profile ID" };
        var yaml = new Option<string>("--yaml") { Description = "Definition source, or @<file>" };
        var nameOpt = new Option<string?>("--name");
        var descriptionOpt = new Option<string?>("--description");
        var output = MohistCliCommands.OutputOption();
        cmd.Options.Add(id); cmd.Options.Add(yaml); cmd.Options.Add(nameOpt); cmd.Options.Add(descriptionOpt); cmd.Options.Add(output);
        var (project, projectId) = AddProjectOptions(cmd);
        cmd.SetAction(async ctx =>
        {
            var profileId = profile is null ? ctx.GetValue(id) : ctx.GetValue(profile) ?? ctx.GetValue(id);
            if (string.IsNullOrWhiteSpace(profileId)) { api.Error.WriteLine("Profile ID is required"); return 1; }
            var expanded = await api.ExpandAtFileAsync(ctx.GetValue(yaml), "--yaml");
            if (expanded is MohistCliApi.ExpandAtFileResult.Failure) return 1;
            var (resolved, resolveExit) = await api.ResolveProject(ctx.GetValue(project), ctx.GetValue(projectId));
            if (resolveExit != 0) return resolveExit;
            var (mode, outputExit) = api.ResolveOutputMode(ctx.GetValue(output));
            if (outputExit != 0) return outputExit;
            var body = new { profileId, name = ctx.GetValue(nameOpt), description = ctx.GetValue(descriptionOpt), definitionSource = ((MohistCliApi.ExpandAtFileResult.Success)expanded).Value };
            return await api.PrintMutationResourceAsync(method, Path(resolved, method == HttpMethod.Post ? null : profileId), body, new ResourceDescriptor(ResourceCardinality.Single, ["profileId", "name", "description", "definitionSource", "sourceProvenance", "isBuiltIn"]), new JsonSelection(JsonSelectionKind.None, [], null), data => mode.StartsWith("json:", StringComparison.Ordinal) ? api.WriteSelectedDataAsync(data, mode, nameof(MohistCliApi.TableShape.WorkflowProfile)) : api.RenderTableAsync(data, MohistCliApi.TableShape.WorkflowProfile));
        });
        return cmd;
    }

    private static Command BuildDelete(MohistCliApi api)
    {
        var cmd = new Command("delete", "Delete a custom Workflow Profile");
        var profile = new Argument<string>("profile") { Description = "Profile ID" };
        var output = MohistCliCommands.OutputOption();
        cmd.Arguments.Add(profile); cmd.Options.Add(output);
        var (project, projectId) = AddProjectOptions(cmd);
        cmd.SetAction(async ctx =>
        {
            var (resolved, resolveExit) = await api.ResolveProject(ctx.GetValue(project), ctx.GetValue(projectId));
            if (resolveExit != 0) return resolveExit;
            var (mode, exit) = api.ResolveOutputMode(ctx.GetValue(output));
            if (exit != 0) return exit;
            return await api.PrintDeleteWithOutputAsync(Path(resolved, ctx.GetValue(profile)), mode, nameof(MohistCliApi.TableShape.WorkflowProfile));
        });
        return cmd;
    }

    private static Command BuildValidate(MohistCliApi api)
    {
        var cmd = new Command("validate", "Validate a local Workflow Definition without contacting a server");
        var file = new Option<string>("--file") { Arity = ArgumentArity.ExactlyOne, Description = "Definition file path, or - for stdin" };
        cmd.Options.Add(file);
        cmd.SetAction(ctx => ValidateLocalAsync(api, ctx.GetValue(file)));
        return cmd;
    }

    private static async Task<int> ValidateLocalAsync(MohistCliApi api, string? file)
    {
        if (string.IsNullOrWhiteSpace(file)) { api.Error.WriteLine("--file is required and must not be empty"); return 1; }
        string source;
        try { source = file == "-" ? await api.StandardInput.ReadToEndAsync() : await api.FileSystem.ReadAllTextAsync(file); }
        catch (Exception ex) { api.Error.WriteLine($"could not read Workflow Definition file: {file} ({ex.Message})"); return 1; }
        var result = Mohist.Workflow.Definition.WorkflowDefinitionParser.Parse(source);
        if (!result.IsValid) { foreach (var error in result.Errors) api.Error.WriteLine($"{error.Path}: {error.Message}"); return 1; }
        api.Output.WriteLine("Workflow Definition is valid.");
        return 0;
    }
}
