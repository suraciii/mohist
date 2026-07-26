using System.CommandLine;

namespace Mohist.Cli;

internal static class ProjectWorkflowCommands
{
    public static Command Build(MohistCliApi api)
    {
        var workflow = new Command("workflow", "Project Workflow references and Prompts");
        workflow.Subcommands.Add(BuildSetDefault(api));
        workflow.Subcommands.Add(BuildPrompt(api));
        return workflow;
    }

    private static Command BuildSetDefault(MohistCliApi api)
    {
        var cmd = new Command("set-default", "Set the Project default Workflow Profile");
        var profile = new Argument<string>("profile") { Description = "Profile ID in this Project" };
        var (project, projectId) = MohistCliCommands.ProjectRefOption();
        var output = MohistCliCommands.OutputOption(ResourceOutputCatalog.For(nameof(MohistCliApi.TableShape.ProjectWorkflowProfile)));
        cmd.Arguments.Add(profile); cmd.Options.Add(project); cmd.Options.Add(projectId); cmd.Options.Add(output);
        cmd.SetAction(async ctx =>
        {
            var (resolved, resolveExit) = await api.ResolveProject(ctx.GetValue(project), ctx.GetValue(projectId));
            if (resolveExit != 0) return resolveExit;
            var (mode, exit) = api.ResolveOutputMode(ctx.GetValue(output));
            if (exit != 0) return exit;
            return await api.PrintPutWithOutputAsync($"/api/projects/{MohistCliCommands.Escape(resolved)}/workflow-profile/default", new { profileId = ctx.GetValue(profile) }, mode, nameof(MohistCliApi.TableShape.ProjectWorkflowProfile));
        });
        return cmd;
    }

    private static Command BuildPrompt(MohistCliApi api)
    {
        var prompt = new Command("prompt", "Manage Project workflow Prompts");
        prompt.Subcommands.Add(BuildPromptGet(api));
        prompt.Subcommands.Add(BuildPromptSet(api));
        prompt.Subcommands.Add(BuildPromptClear(api));
        prompt.Subcommands.Add(BuildPromptPreview(api));
        return prompt;
    }

    private static (Option<string?> Project, Option<string?> ProjectId, Option<string?> Output) AddOptions(Command cmd, MohistCliApi.TableShape shape)
    {
        var (project, projectId) = MohistCliCommands.ProjectRefOption();
        var output = MohistCliCommands.OutputOption(ResourceOutputCatalog.For(nameof(shape)));
        cmd.Options.Add(project); cmd.Options.Add(projectId); cmd.Options.Add(output);
        return (project, projectId, output);
    }

    private static Command BuildPromptGet(MohistCliApi api)
    {
        var cmd = new Command("get", "Get Project Prompts");
        var (project, projectId, output) = AddOptions(cmd, MohistCliApi.TableShape.WorkflowProfilePrompt);
        cmd.SetAction(async ctx =>
        {
            var (resolved, exit) = await api.ResolveProject(ctx.GetValue(project), ctx.GetValue(projectId));
            if (exit != 0) return exit;
            var (mode, modeExit) = api.ResolveOutputMode(ctx.GetValue(output));
            return modeExit != 0 ? modeExit : await api.PrintWithOutputAsync($"/api/projects/{MohistCliCommands.Escape(resolved)}/workflow-profile/prompts", mode, nameof(MohistCliApi.TableShape.WorkflowProfilePrompt));
        });
        return cmd;
    }

    private static Command BuildPromptSet(MohistCliApi api)
    {
        var cmd = new Command("set", "Set a Project Prompt");
        var key = new Argument<string>("key");
        var body = new Option<string>("--body") { Description = "Prompt body, or @<file>" };
        cmd.Arguments.Add(key); cmd.Options.Add(body);
        var (project, projectId, output) = AddOptions(cmd, MohistCliApi.TableShape.WorkflowProfilePrompt);
        cmd.SetAction(async ctx =>
        {
            var expanded = await api.ExpandAtFileAsync(ctx.GetValue(body), "--body");
            if (expanded is MohistCliApi.ExpandAtFileResult.Failure) return 1;
            var (resolved, exit) = await api.ResolveProject(ctx.GetValue(project), ctx.GetValue(projectId));
            if (exit != 0) return exit;
            var (mode, modeExit) = api.ResolveOutputMode(ctx.GetValue(output));
            if (modeExit != 0) return modeExit;
            var value = ((MohistCliApi.ExpandAtFileResult.Success)expanded).Value;
            return await api.PrintPutWithOutputAsync($"/api/projects/{MohistCliCommands.Escape(resolved)}/workflow-profile/prompts/{Uri.EscapeDataString(ctx.GetValue(key)!)}", new { body = value }, mode, nameof(MohistCliApi.TableShape.WorkflowProfilePrompt));
        });
        return cmd;
    }

    private static Command BuildPromptClear(MohistCliApi api)
    {
        var cmd = new Command("clear", "Clear a Project Prompt");
        var key = new Argument<string>("key"); cmd.Arguments.Add(key);
        var (project, projectId, output) = AddOptions(cmd, MohistCliApi.TableShape.WorkflowProfilePrompt);
        cmd.SetAction(async ctx =>
        {
            var (resolved, exit) = await api.ResolveProject(ctx.GetValue(project), ctx.GetValue(projectId));
            if (exit != 0) return exit;
            var (mode, modeExit) = api.ResolveOutputMode(ctx.GetValue(output));
            return modeExit != 0 ? modeExit : await api.PrintDeleteWithOutputAsync($"/api/projects/{MohistCliCommands.Escape(resolved)}/workflow-profile/prompts/{Uri.EscapeDataString(ctx.GetValue(key)!)}", mode, nameof(MohistCliApi.TableShape.WorkflowProfilePrompt));
        });
        return cmd;
    }

    private static Command BuildPromptPreview(MohistCliApi api)
    {
        var cmd = new Command("preview", "Preview a Project Prompt");
        var key = new Argument<string>("key"); cmd.Arguments.Add(key);
        var (project, projectId, output) = AddOptions(cmd, MohistCliApi.TableShape.WorkflowProfilePreview);
        cmd.SetAction(async ctx =>
        {
            var (resolved, exit) = await api.ResolveProject(ctx.GetValue(project), ctx.GetValue(projectId));
            if (exit != 0) return exit;
            var (mode, modeExit) = api.ResolveOutputMode(ctx.GetValue(output));
            return modeExit != 0 ? modeExit : await api.PrintPostWithOutputAsync($"/api/projects/{MohistCliCommands.Escape(resolved)}/workflow-profile/prompts/{Uri.EscapeDataString(ctx.GetValue(key)!)}/preview", new { }, mode, nameof(MohistCliApi.TableShape.WorkflowProfilePreview));
        });
        return cmd;
    }
}
