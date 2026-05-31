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

        return project;
    }

    private static Command BuildList(MohistCliApi api)
    {
        var cmd = new Command("list", "List all projects");
        cmd.Aliases.Add("ls");
        cmd.SetAction((ParseResult _) => api.PrintGetAsync("/api/projects"));
        return cmd;
    }

    private static Command BuildCreate(MohistCliApi api)
    {
        var cmd = new Command("create", "Create a new project");
        var nameArg = new Argument<string>("name") { Description = "Project name" };
        var pathOpt = new Option<string?>("--path", "-p") { Description = "Project path" };
        var baseBranchOpt = new Option<string?>("--base-branch", "-b") { Description = "Base branch name" };
        cmd.Arguments.Add(nameArg);
        cmd.Options.Add(pathOpt);
        cmd.Options.Add(baseBranchOpt);
        cmd.SetAction(ctx =>
        {
            var name = ctx.GetValue(nameArg);
            var path = ctx.GetValue(pathOpt) ?? Environment.CurrentDirectory;
            var baseBranch = ctx.GetValue(baseBranchOpt);
            return api.PrintPostAsync("/api/projects", new { name, path, baseBranch });
        });
        return cmd;
    }

    private static Command BuildShow(MohistCliApi api)
    {
        var cmd = new Command("show", "Show project details");
        var identifierArg = new Argument<string>("project") { Description = "Project name or ID" };
        cmd.Arguments.Add(identifierArg);
        cmd.SetAction(ctx =>
        {
            var identifier = ctx.GetValue(identifierArg);
            return api.PrintGetAsync($"/api/projects/{MohistCliCommands.Escape(identifier!)}");
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
