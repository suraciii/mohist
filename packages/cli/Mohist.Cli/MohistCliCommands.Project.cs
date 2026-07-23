using System.CommandLine;
using System.CommandLine.Parsing;

namespace Mohist.Cli;

internal static class ProjectCommands
{
    public static Command Build(MohistCliApi api)
    {
        var project = new Command("project", "Project management");

        project.Subcommands.Add(BuildList(api));
        project.Subcommands.Add(BuildCreate(api));
        project.Subcommands.Add(BuildShow(api));
        project.Subcommands.Add(BuildUse(api));
        project.Subcommands.Add(BuildDelete(api));
        project.Subcommands.Add(ProjectWorkflowCommands.Build(api));
        project.Subcommands.Add(VariableCommands.BuildVariableGroup(api, VariableScopeKind.Project));

        return project;
    }

    private static Command BuildList(MohistCliApi api)
    {
        var cmd = new Command("list", "List all projects");
        cmd.Aliases.Add("ls");
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var output = ctx.GetValue(outputOpt);
            var (mode, exit) = api.ResolveOutputMode(output);

            if (exit != 0) return Task.FromResult(exit);

            return api.PrintWithOutputAsync(
                "/api/projects",
                mode,
                nameof(MohistCliApi.TableShape.ProjectList));
        });
        return cmd;
    }

    private static Command BuildCreate(MohistCliApi api)
    {
        var cmd = new Command("create", "Create a new project from a local Git working tree");
        var nameArg = new Argument<string>("name") { Description = "Project name" };
        var pathOpt = new Option<string>("--path") { Description = "Path to the local Git repository that becomes the project's default repository" };
        cmd.Arguments.Add(nameArg);
        cmd.Options.Add(pathOpt);
        cmd.SetAction(async ctx =>
        {
            var name = ctx.GetValue(nameArg);
            var path = ctx.GetValue(pathOpt);

            var bootstrap = await ProjectRepositoryBootstrap.TryResolveAsync(
                path ?? string.Empty,
                api.FileSystem,
                api.CommandExecutor);

            if (bootstrap is ProjectRepositoryBootstrap.Outcome.Failure failure)
            {
                api.Error.WriteLine(failure.Message);
                return 1;
            }

            var resolved = ((ProjectRepositoryBootstrap.Outcome.Success)bootstrap).Result;
            return await api.PrintPostAsync(
                "/api/projects",
                new
                {
                    name,
                    repository = new
                    {
                        name = resolved.RepositoryName,
                        gitUrl = resolved.GitUrl,
                        baseBranch = resolved.BaseBranch,
                    },
                });
        });
        return cmd;
    }

    private static Command BuildShow(MohistCliApi api)
    {
        var cmd = new Command("show", "Show project details");
        var identifierArg = new Argument<string>("project") { Description = "Project name or ID" };
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Arguments.Add(identifierArg);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var identifier = ctx.GetValue(identifierArg);
            var output = ctx.GetValue(outputOpt);
            var (mode, exit) = api.ResolveOutputMode(output);

            if (exit != 0) return Task.FromResult(exit);

            return api.PrintWithOutputAsync(
                $"/api/projects/{MohistCliCommands.Escape(identifier!)}",
                mode,
                nameof(MohistCliApi.TableShape.ProjectShow));
        });
        return cmd;
    }

    private static Command BuildUse(MohistCliApi api)
    {
        var cmd = new Command("use", "Set active project");
        var identifierArg = new Argument<string>("project") { Description = "Project name or ID" };
        cmd.Arguments.Add(identifierArg);
        cmd.SetAction(ctx =>
        {
            var identifier = ctx.GetValue(identifierArg);
            return api.UseProjectAsync(identifier!);
        });
        return cmd;
    }

    private static Command BuildDelete(MohistCliApi api)
    {
        var cmd = new Command("delete", "Delete a project");
        cmd.Aliases.Add("remove");
        cmd.Aliases.Add("rm");
        var identifierArg = new Argument<string>("project") { Description = "Project name or ID" };
        cmd.Arguments.Add(identifierArg);
        cmd.SetAction(ctx =>
        {
            var identifier = ctx.GetValue(identifierArg);
            return api.PrintDeleteAsync($"/api/projects/{MohistCliCommands.Escape(identifier!)}");
        });
        return cmd;
    }

}
