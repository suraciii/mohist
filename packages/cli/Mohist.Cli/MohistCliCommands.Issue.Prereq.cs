using System.CommandLine;

namespace Mohist.Cli;

internal static partial class IssueCommands
{
    private static Command BuildPrereq(MohistCliApi api)
    {
        var prereq = new Command("prereq", "Manage issue start prerequisites");
        prereq.Subcommands.Add(BuildPrereqAdd(api));
        prereq.Subcommands.Add(BuildPrereqRemove(api));
        return prereq;
    }

    private static Command BuildPrereqAdd(MohistCliApi api)
    {
        var cmd = new Command("add", "Add a start prerequisite to an issue");
        var numberArg = NumberArg();
        var prereqNumberArg = new Argument<int>("prereq-number")
        {
            Description = "Prerequisite issue number",
        };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Arguments.Add(numberArg);
        cmd.Arguments.Add(prereqNumberArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var prereqNumber = ctx.GetValue(prereqNumberArg);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            return AddAsync();

            async Task<int> AddAsync()
            {
                var (resolvedProjectId, resolveExit) = await ResolveProjectId(api, project, projectId);
                if (resolveExit != 0) return resolveExit;
                var (mode, exit) = ValidateOutput(api, output);
                if (exit != 0) return exit;
                var path = ProjectIssuesPath(resolvedProjectId, $"/issues/{MohistCliCommands.Escape(number!)}/prerequisites");
                return await api.PrintPostWithOutputAsync(
                    path,
                    new { prerequisiteNumber = prereqNumber },
                    mode,
                    nameof(MohistCliApi.TableShape.IssueShow));
            }
        });
        return cmd;
    }

    private static Command BuildPrereqRemove(MohistCliApi api)
    {
        var cmd = new Command("remove", "Remove a start prerequisite from an issue");
        var numberArg = NumberArg();
        var prereqNumberArg = new Argument<int>("prereq-number")
        {
            Description = "Prerequisite issue number",
        };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Arguments.Add(numberArg);
        cmd.Arguments.Add(prereqNumberArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var prereqNumber = ctx.GetValue(prereqNumberArg);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            return RemoveAsync();

            async Task<int> RemoveAsync()
            {
                var (resolvedProjectId, resolveExit) = await ResolveProjectId(api, project, projectId);
                if (resolveExit != 0) return resolveExit;
                var (mode, exit) = ValidateOutput(api, output);
                if (exit != 0) return exit;
                var path = ProjectIssuesPath(
                    resolvedProjectId,
                    $"/issues/{MohistCliCommands.Escape(number!)}/prerequisites/{prereqNumber}");
                return await api.PrintDeleteWithOutputAsync(
                    path,
                    mode,
                    nameof(MohistCliApi.TableShape.IssueShow));
            }
        });
        return cmd;
    }
}