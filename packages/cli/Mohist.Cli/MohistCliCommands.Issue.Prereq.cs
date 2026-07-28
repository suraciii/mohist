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
        var projectOpt = MohistCliCommands.ProjectRefOption();
        var jsonOpt = MohistCliCommands.JsonSelectionOption(IssueDescriptor);
        cmd.Arguments.Add(numberArg);
        cmd.Arguments.Add(prereqNumberArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(jsonOpt);
        cmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var prereqNumber = ctx.GetValue(prereqNumberArg);
            var project = ctx.GetValue(projectOpt);
            var selection = JsonSelection.Parse(IssueDescriptor, ctx.GetResult(jsonOpt) is not null, ctx.GetValue(jsonOpt));
            return AddAsync();

            async Task<int> AddAsync()
            {
                if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
                    return api.WriteJsonSelectionResult(IssueDescriptor, selection);
                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project);
                if (resolveExit != 0) return resolveExit;
                var path = ProjectIssuesPath(resolvedProjectId, $"/issues/{MohistCliCommands.Escape(number!)}/prerequisites");
                return await api.PrintMutationResourceAsync(
                    HttpMethod.Post,
                    path,
                    new { prerequisiteNumber = prereqNumber },
                    IssueDescriptor,
                    selection,
                    data => api.RenderTableAsync(data, MohistCliApi.TableShape.Issue));
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
        var projectOpt = MohistCliCommands.ProjectRefOption();
        var jsonOpt = MohistCliCommands.JsonSelectionOption(IssueDescriptor);
        cmd.Arguments.Add(numberArg);
        cmd.Arguments.Add(prereqNumberArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(jsonOpt);
        cmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var prereqNumber = ctx.GetValue(prereqNumberArg);
            var project = ctx.GetValue(projectOpt);
            var selection = JsonSelection.Parse(IssueDescriptor, ctx.GetResult(jsonOpt) is not null, ctx.GetValue(jsonOpt));
            return RemoveAsync();

            async Task<int> RemoveAsync()
            {
                if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
                    return api.WriteJsonSelectionResult(IssueDescriptor, selection);
                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project);
                if (resolveExit != 0) return resolveExit;
                var path = ProjectIssuesPath(
                    resolvedProjectId,
                    $"/issues/{MohistCliCommands.Escape(number!)}/prerequisites/{prereqNumber}");
                return await api.PrintMutationResourceAsync(
                    HttpMethod.Delete,
                    path,
                    null,
                    IssueDescriptor,
                    selection,
                    data => api.RenderTableAsync(data, MohistCliApi.TableShape.Issue));
            }
        });
        return cmd;
    }
}
