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
        workflow.Subcommands.Add(BuildWorkflowConfig(api));
        return workflow;
    }

    private static Command BuildWorkflowConfig(MohistCliApi api)
    {
        var config = new Command("config", "Issue workflow configuration overrides (template / variables)");
        config.Subcommands.Add(BuildWorkflowConfigGet(api));
        config.Subcommands.Add(BuildWorkflowConfigSet(api));
        config.Subcommands.Add(BuildWorkflowConfigClear(api));
        return config;
    }

    private static Command BuildWorkflowConfigGet(MohistCliApi api)
    {
        var cmd = new Command("get", "Show the issue's workflow profile (template / variables)");
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
                var (mode, exit) = api.ResolveOutputMode(output);
                if (exit != 0) return exit;
                var localExit = api.HandleLocalJsonSelection(mode, nameof(MohistCliApi.TableShape.WorkflowProfile));
                if (localExit is not null) return localExit.Value;
                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);
                if (resolveExit != 0) return resolveExit;

                var profilePath = ProjectIssuesPath(
                    resolvedProjectId, $"/issues/{MohistCliCommands.Escape(number!)}/workflow-profile");
                return await PrintWorkflowProfileAsync(api, profilePath, mode);
            }
        });
        return cmd;
    }

    private static async Task<int> PrintWorkflowProfileAsync(MohistCliApi api, string profilePath, string mode)
    {
        var (exitCode, dataNode) = await api.GetDataOrPrintErrorAsync(profilePath);
        if (exitCode != 0)
            return exitCode;
        if (dataNode is null)
            return 1;

        return await api.WriteSelectedDataAsync(dataNode, mode, nameof(MohistCliApi.TableShape.WorkflowProfile));
    }

    private static Command BuildWorkflowConfigClear(MohistCliApi api)
    {
        var cmd = new Command("clear", "Clear the issue workflow template override");
        var numberArg = NumberArg();
        var templateOpt = new Option<bool>("--template")
        {
            Description = "Remove the issue's template override (DELETE /workflow-profile/template)",
        };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Arguments.Add(numberArg);
        cmd.Options.Add(templateOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            var templateProvided = IsOptionProvided(ctx, templateOpt);
            return ClearAsync();

            async Task<int> ClearAsync()
            {
                if (!templateProvided)
                {
                    api.Error.WriteLine("nothing to clear — pass --template");
                    return 1;
                }

                var (mode, exit) = api.ResolveOutputMode(output);
                if (exit != 0) return exit;
                var localExit = api.HandleLocalJsonSelection(mode, nameof(MohistCliApi.TableShape.WorkflowVariables));
                if (localExit is not null) return localExit.Value;
                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);
                if (resolveExit != 0) return resolveExit;

                var issuePath = ProjectIssuesPath(
                    resolvedProjectId,
                    $"/issues/{MohistCliCommands.Escape(number!)}/workflow-profile");

                var deleteExit = await api.PrintDeleteWithOutputAsync(
                    issuePath + "/template",
                    mode,
                    nameof(MohistCliApi.TableShape.WorkflowProfile));
                if (deleteExit != 0)
                    return deleteExit;

                return 0;
            }
        });
        return cmd;
    }
}
