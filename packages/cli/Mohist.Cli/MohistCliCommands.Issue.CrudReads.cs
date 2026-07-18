using System.CommandLine;

namespace Mohist.Cli;

internal static partial class IssueCommands
{
    private static Command BuildList(MohistCliApi api)
    {
        var cmd = new Command("list", "List issues");
        cmd.Aliases.Add("ls");
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var stageOpt = MohistCliCommands.StageOption();
        var labelOpt = MohistCliCommands.LabelFilterOption();
        var priorityOpt = MohistCliCommands.PriorityOption();
        var repositoryOpt = new Option<string?>("--repo") { Description = "Filter by target repository name" };
        var parentOpt = new Option<int?>("--parent") { Description = "Filter by parent issue number" };
        var allOpt = new Option<bool>("--all") { Description = "Show all issues" };
        var archivedOpt = new Option<bool>("--archived") { Description = "Show archived issues" };
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(stageOpt);
        cmd.Options.Add(labelOpt);
        cmd.Options.Add(priorityOpt);
        cmd.Options.Add(repositoryOpt);
        cmd.Options.Add(parentOpt);
        cmd.Options.Add(allOpt);
        cmd.Options.Add(archivedOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var stage = ctx.GetValue(stageOpt);
            var labels = ctx.GetValue(labelOpt);
            var priority = ctx.GetValue(priorityOpt);
            var repository = ctx.GetValue(repositoryOpt);
            var parent = ctx.GetValue(parentOpt);
            var all = ctx.GetValue(allOpt);
            var archived = ctx.GetValue(archivedOpt);
            var output = ctx.GetValue(outputOpt);
            return ListAsync();

            async Task<int> ListAsync()
            {
                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);
                if (resolveExit != 0) return resolveExit;
                if (labels is { Length: > 0 })
                {
                    var labelError = LabelDelta.ValidateFilterTokens(labels);
                    if (labelError is not null)
                    {
                        api.Error.WriteLine(labelError);
                        return 1;
                    }
                }
                var query = MohistCliCommands.Query(
                    Stage: stage,
                    Labels: labels,
                    Priority: priority,
                    Repository: repository,
                    Parent: parent,
                    Archived: archived ? true : null,
                    All: all ? true : null);
                var (mode, exit) = api.ResolveOutputMode(output);
                if (exit != 0) return exit;
                return await api.PrintWithOutputAsync(
                    ProjectIssuesPath(resolvedProjectId, "/issues") + query,
                    mode,
                    nameof(MohistCliApi.TableShape.IssueList));
            }
        });
        return cmd;
    }

    private static Command BuildShow(MohistCliApi api)
    {
        var cmd = new Command("show", "Show issue details");
        var numberArg = NumberArg();
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Arguments.Add(numberArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            return GetAsync();

            async Task<int> GetAsync()
            {
                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);
                if (resolveExit != 0) return resolveExit;
                var (mode, exit) = api.ResolveOutputMode(output);
                if (exit != 0) return exit;
                return await api.PrintWithOutputAsync(
                    ProjectIssuesPath(resolvedProjectId, $"/issues/{MohistCliCommands.Escape(number!)}"),
                    mode,
                    nameof(MohistCliApi.TableShape.IssueShow));
            }
        });
        return cmd;
    }
}
