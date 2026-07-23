using System.CommandLine;
namespace Mohist.Cli;

internal static partial class IssueCommands
{
    private static Command BuildWorkflow(MohistCliApi api)
    {
        var workflow = new Command("workflow", "Issue workflow actions");
        var numberArg = new Argument<string>("number") { Description = "Issue number" };

        var statusCmd = new Command("status", "Show workflow status");
        var (statusProjectOpt, statusProjectIdOpt) = MohistCliCommands.ProjectRefOption();
        var statusOutputOpt = MohistCliCommands.OutputOption();
        statusCmd.Arguments.Add(numberArg);
        statusCmd.Options.Add(statusProjectOpt);
        statusCmd.Options.Add(statusProjectIdOpt);
        statusCmd.Options.Add(statusOutputOpt);
        statusCmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var project = ctx.GetValue(statusProjectOpt);
            var projectId = ctx.GetValue(statusProjectIdOpt);
            var output = ctx.GetValue(statusOutputOpt);
            return StatusAsync();

            async Task<int> StatusAsync()
            {
                var (mode, exit) = api.ResolveOutputMode(output);
                if (exit != 0) return exit;
                var localExit = api.HandleLocalJsonSelection(mode, nameof(MohistCliApi.TableShape.WorkflowStatus));
                if (localExit is not null) return localExit.Value;
                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);
                if (resolveExit != 0) return resolveExit;
                return await api.PrintWithOutputAsync(
                    ProjectIssuesPath(resolvedProjectId, $"/issues/{MohistCliCommands.Escape(number!)}/workflow/status"),
                    mode,
                    nameof(MohistCliApi.TableShape.WorkflowStatus));
            }
        });

        var timelineCmd = new Command("timeline", "Show workflow timeline");
        var (timelineProjectOpt, timelineProjectIdOpt) = MohistCliCommands.ProjectRefOption();
        timelineCmd.Arguments.Add(numberArg);
        timelineCmd.Options.Add(timelineProjectOpt);
        timelineCmd.Options.Add(timelineProjectIdOpt);
        timelineCmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var project = ctx.GetValue(timelineProjectOpt);
            var projectId = ctx.GetValue(timelineProjectIdOpt);
            return TimelineAsync();

            async Task<int> TimelineAsync()
            {
                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);
                if (resolveExit != 0) return resolveExit;
                return await api.PrintGetAsync(
                    ProjectIssuesPath(resolvedProjectId, $"/issues/{MohistCliCommands.Escape(number!)}/workflow/timeline"));
            }
        });

        workflow.Subcommands.Add(statusCmd);
        workflow.Subcommands.Add(timelineCmd);
        return workflow;
    }
}
