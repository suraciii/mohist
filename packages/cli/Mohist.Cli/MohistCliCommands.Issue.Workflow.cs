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
        var cmd = new Command("clear", "Composite config removals (template / variables)");
        var numberArg = NumberArg();
        var templateOpt = new Option<bool>("--template")
        {
            Description = "Remove the issue's template override (DELETE /workflow-profile/template)",
        };
        var varOpt = new Option<string[]?>("--var")
        {
            Description = "Remove a variable by key. Use '<stage>.k' for stage-scoped variables. Repeatable; merged into one PATCH /workflow-profile/variables with each key set to null.",
            AllowMultipleArgumentsPerToken = true,
        };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Arguments.Add(numberArg);
        cmd.Options.Add(templateOpt);
        cmd.Options.Add(varOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var vars = ctx.GetValue(varOpt);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            var templateProvided = IsOptionProvided(ctx, templateOpt);
            var varProvided = ctx.GetResult(varOpt) is not null;
            return ClearAsync();

            async Task<int> ClearAsync()
            {
                var hasAnyClear = templateProvided || varProvided;
                if (!hasAnyClear)
                {
                    api.Error.WriteLine("nothing to clear — pass at least one of --template or --var");
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

                var varsPatchBody = new Dictionary<string, object?>(StringComparer.Ordinal);

                if (varProvided)
                {
                    var varsPayload = new Dictionary<string, object?>(StringComparer.Ordinal);
                    var stagesPayload = new Dictionary<string, object?>(StringComparer.Ordinal);
                    foreach (var entry in vars!)
                    {
                        var key = entry;
                        if (string.IsNullOrWhiteSpace(key))
                        {
                            api.Error.WriteLine($"--var '{entry}' has an empty key");
                            return 1;
                        }
                        var dot = key.IndexOf('.');
                        if (dot > 0 && dot < key.Length - 1)
                        {
                            var stage = key[..dot];
                            var stageKey = key[(dot + 1)..];
                            if (!stagesPayload.TryGetValue(stage, out var existing))
                            {
                                existing = new Dictionary<string, object?>(StringComparer.Ordinal)
                                {
                                    ["vars"] = new Dictionary<string, object?>(StringComparer.Ordinal),
                                };
                                stagesPayload[stage] = existing;
                            }
                            var stageObj = (Dictionary<string, object?>)existing!;
                            var stageVars = (Dictionary<string, object?>)stageObj["vars"]!;
                            stageVars[stageKey] = null;
                        }
                        else
                        {
                            varsPayload[key] = null;
                        }
                    }
                    if (varsPayload.Count > 0)
                        varsPatchBody["vars"] = varsPayload;
                    if (stagesPayload.Count > 0)
                        varsPatchBody["stages"] = stagesPayload;
                }

                if (varProvided)
                {
                    var patchExit = await api.PrintPatchWithOutputAsync(
                        issuePath + "/variables",
                        varsPatchBody,
                        mode,
                        nameof(MohistCliApi.TableShape.WorkflowVariables));
                    if (patchExit != 0)
                        return patchExit;
                }

                if (templateProvided)
                {
                    var deleteExit = await api.PrintDeleteWithOutputAsync(
                        issuePath + "/template",
                        mode,
                        nameof(MohistCliApi.TableShape.WorkflowProfile));
                    if (deleteExit != 0)
                        return deleteExit;
                }

                return 0;
            }
        });
        return cmd;
    }
}
