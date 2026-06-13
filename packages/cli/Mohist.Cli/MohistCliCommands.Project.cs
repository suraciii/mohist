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
        project.Subcommands.Add(ProjectRepoCommands.Build(api));

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
            var validation = MohistCliApi.ValidateOutputMode(output);
            if (validation is MohistCliApi.OutputModeResult.Invalid invalid)
            {
                api.Error.WriteLine(invalid.Message);
                return Task.FromResult(1);
            }
            var mode = ((MohistCliApi.OutputModeResult.Valid)validation).Mode;
            return api.PrintWithOutputAsync(
                "/api/projects",
                mode,
                nameof(MohistCliApi.TableShape.ProjectList));
        });
        return cmd;
    }

    private static Command BuildCreate(MohistCliApi api)
    {
        var cmd = new Command("create", "Create a new project");
        var nameArg = new Argument<string>("name") { Description = "Project name" };
        cmd.Arguments.Add(nameArg);
        cmd.SetAction(ctx =>
        {
            var name = ctx.GetValue(nameArg);
            return api.PrintPostAsync("/api/projects", new { name });
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
            var validation = MohistCliApi.ValidateOutputMode(output);
            if (validation is MohistCliApi.OutputModeResult.Invalid invalid)
            {
                api.Error.WriteLine(invalid.Message);
                return Task.FromResult(1);
            }
            var mode = ((MohistCliApi.OutputModeResult.Valid)validation).Mode;
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
