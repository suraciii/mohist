using System.CommandLine;

namespace Mohist.Cli;

internal static partial class IssueCommands
{
    private static Command BuildTemplate(MohistCliApi api)
    {
        var template = new Command("template", "Issue template management");
        template.Subcommands.Add(BuildTemplateList(api));
        template.Subcommands.Add(BuildTemplateGet(api));
        return template;
    }

    private static Command BuildTemplateList(MohistCliApi api)
    {
        var cmd = new Command("list", "List available issue templates for the active project");
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
                var (resolvedProjectId, resolveExit) = await ResolveProjectId(api, project, projectId);
                if (resolveExit != 0) return resolveExit;
                var (mode, exit) = ValidateOutput(api, output);
                if (exit != 0) return exit;
                return await api.PrintWithOutputAsync(
                    IssueTemplatesPath(resolvedProjectId, "/"),
                    mode,
                    nameof(MohistCliApi.TableShape.IssueTemplateList));
            }
        });
        return cmd;
    }

    private static Command BuildTemplateGet(MohistCliApi api)
    {
        var cmd = new Command("get", "Show a single issue template by name");
        var nameArg = new Argument<string>("name") { Description = "Template name or id (e.g. feature)" };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Arguments.Add(nameArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var name = ctx.GetValue(nameArg);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            return GetAsync();

            async Task<int> GetAsync()
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    api.Error.WriteLine("Template name is required");
                    return 1;
                }
                var (resolvedProjectId, resolveExit) = await ResolveProjectId(api, project, projectId);
                if (resolveExit != 0) return resolveExit;
                var (mode, exit) = ValidateOutput(api, output);
                if (exit != 0) return exit;
                return await api.PrintWithOutputAsync(
                    IssueTemplatesPath(resolvedProjectId, $"/{name}"),
                    mode,
                    nameof(MohistCliApi.TableShape.IssueTemplateShow));
            }
        });
        return cmd;
    }
}