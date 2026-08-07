using System.CommandLine;
using System.CommandLine.Parsing;

namespace Mohist.Cli;

internal static partial class RunCommands
{
    internal static readonly ResourceDescriptor RunControlDescriptor = new(
        ResourceCardinality.Single,
        [
            "workflowRunId",
            "approved",
            "rejected",
            "retried",
            "rerun",
            "rerunFromStage",
            "paused",
            "resumed",
            "stopped",
            "status",
            "stage",
            "issueRef",
            "decidedBy",
            "displayName",
        ]);

    public static Command Build(MohistCliApi api)
    {
        var run = new Command("run", "Workflow run navigation and control");

        run.Subcommands.Add(BuildApprove(api));
        run.Subcommands.Add(BuildReject(api));
        run.Subcommands.Add(BuildRetry(api));
        run.Subcommands.Add(BuildRerun(api));
        run.Subcommands.Add(BuildPause(api));
        run.Subcommands.Add(BuildResume(api));
        run.Subcommands.Add(BuildStop(api));

        RegisterReads(run, api);
        RegisterFeedback(run, api);
        run.Subcommands.Add(VariableCommands.BuildVariableGroup(api, VariableScopeKind.Run));

        return run;
    }

    internal static async Task<(string? RunId, int Exit)> ResolveRunTargetAsync(
        MohistCliApi api,
        string? runId,
        string? issueNumber,
        string? project)
    {
        var shapeExit = await ValidateRunTargetShapeAsync(api, runId, issueNumber).ConfigureAwait(false);
        if (shapeExit != 0)
            return (null, shapeExit);

        var hasRunId = !string.IsNullOrWhiteSpace(runId);

        if (hasRunId)
            return (runId, 0);

        var (resolvedProjectId, resolveExit) = await api.ResolveProject(project).ConfigureAwait(false);
        if (resolveExit != 0)
            return (null, resolveExit);

        var issuePath = $"/api/projects/{MohistCliCommands.Escape(resolvedProjectId)}/issues/{MohistCliCommands.Escape(issueNumber!)}";
        var (issueExit, issueData) = await api.GetDataOrPrintErrorAsync(issuePath).ConfigureAwait(false);
        if (issueExit != 0)
            return (null, issueExit);

        var workflowRunId = issueData?["workflowRunId"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(workflowRunId))
        {
            await api.Error.WriteLineAsync(
                $"Issue #{issueNumber} has no active workflow run.").ConfigureAwait(false);
            return (null, CliExitCode.For(CliExitOutcome.OperationFailure));
        }

        return (workflowRunId, 0);
    }

    private static async Task<int> ValidateRunTargetShapeAsync(
        MohistCliApi api,
        string? runId,
        string? issueNumber)
    {
        var hasRunId = !string.IsNullOrWhiteSpace(runId);
        var hasIssue = !string.IsNullOrWhiteSpace(issueNumber);

        if (hasRunId && hasIssue)
        {
            await api.Error.WriteLineAsync(
                "Provide either a Run ID or --issue, not both.").ConfigureAwait(false);
            return CliExitCode.For(CliExitOutcome.UsageFailure);
        }

        if (!hasRunId && !hasIssue)
        {
            await api.Error.WriteLineAsync(
                "A Run ID or --issue <number> is required.").ConfigureAwait(false);
            return CliExitCode.For(CliExitOutcome.UsageFailure);
        }

        return 0;
    }


    private static Argument<string?> RunIdArg() => new("run-id")
    {
        Description = "Workflow run id (or use --issue <number>)",
        Arity = ArgumentArity.ZeroOrOne,
        DefaultValueFactory = _ => null,
    };

    private static Option<string?> IssueOption() => new("--issue")
    {
        Description = "Target the workflow run bound to this issue number",
    };

    private static Option<string?> ProjectOptions() =>
        MohistCliCommands.ProjectRefOption();

    private static string WorkflowRunPath(string runId, string suffix) =>
        $"/api/workflow-runs/{MohistCliCommands.Escape(runId)}{suffix}";


    private static Command BuildApprove(MohistCliApi api)
    {
        var cmd = new Command("approve", "Pass the approval gate for a workflow run");
        var runIdArg = RunIdArg();
        var issueOpt = IssueOption();
        var projectOpt = ProjectOptions();
        var displayNameOpt = new Option<string?>("--display-name")
        {
            Description = "Optional display alias (1-100 characters); the approver is the authenticated identity",
        };
        var jsonOpt = MohistCliCommands.JsonSelectionOption(RunControlDescriptor);
        cmd.Arguments.Add(runIdArg);
        cmd.Options.Add(issueOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(displayNameOpt);
        cmd.Options.Add(jsonOpt);
        cmd.SetAction(ctx =>
        {
            var runId = ctx.GetValue(runIdArg);
            var issue = ctx.GetValue(issueOpt);
            var project = ctx.GetValue(projectOpt);
            var displayName = ctx.GetValue(displayNameOpt);
            var jsonProvided = ctx.GetResult(jsonOpt) is not null;
            var json = ctx.GetValue(jsonOpt);
            var selection = JsonSelection.Parse(RunControlDescriptor, jsonProvided, json);
            return ActAsync();

            async Task<int> ActAsync()
            {
                if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
                    return api.WriteJsonSelectionResult(RunControlDescriptor, selection);

                var normalizedDisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
                if (normalizedDisplayName?.Length > 100)
                {
                    api.Error.WriteLine("--display-name must be 100 characters or fewer.");
                    return 1;
                }

                var (resolvedRunId, resolveExit) = await ResolveRunTargetAsync(
                    api, runId, issue, project).ConfigureAwait(false);
                if (resolveExit != 0)
                    return resolveExit;

                return await api.PrintMutationResourceAsync(
                    HttpMethod.Post,
                    WorkflowRunPath(resolvedRunId!, "/approve"),
                    new { displayName = normalizedDisplayName },
                    RunControlDescriptor,
                    selection,
                    data => api.RenderTableAsync(data, MohistCliApi.TableShape.WorkflowRunDetail));
            }
        });
        return cmd;
    }


    private static Command BuildReject(MohistCliApi api)
    {
        var cmd = new Command(
            "reject",
            "Reject the workflow run at its approval gate with a reason (use --message; required)");
        var runIdArg = RunIdArg();
        var issueOpt = IssueOption();
        var projectOpt = ProjectOptions();
        var messageOpt = new Option<string?>("--message", "-m")
        {
            Description = "Reject reason / change request message (required, must not be empty)",
        };
        var displayNameOpt = new Option<string?>("--display-name")
        {
            Description = "Optional display alias (1-100 characters); the approver is the authenticated identity",
        };
        var jsonOpt = MohistCliCommands.JsonSelectionOption(RunControlDescriptor);
        cmd.Arguments.Add(runIdArg);
        cmd.Options.Add(issueOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(messageOpt);
        cmd.Options.Add(displayNameOpt);
        cmd.Options.Add(jsonOpt);
        cmd.SetAction(ctx =>
        {
            var runId = ctx.GetValue(runIdArg);
            var issue = ctx.GetValue(issueOpt);
            var project = ctx.GetValue(projectOpt);
            var message = ctx.GetValue(messageOpt);
            var displayName = ctx.GetValue(displayNameOpt);
            var jsonProvided = ctx.GetResult(jsonOpt) is not null;
            var json = ctx.GetValue(jsonOpt);
            var selection = JsonSelection.Parse(RunControlDescriptor, jsonProvided, json);
            return RejectAsync();

            async Task<int> RejectAsync()
            {
                if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
                    return api.WriteJsonSelectionResult(RunControlDescriptor, selection);

                if (string.IsNullOrWhiteSpace(message))
                {
                    await api.Error.WriteLineAsync(
                        "--message is required and must not be empty").ConfigureAwait(false);
                    return CliExitCode.For(CliExitOutcome.OperationFailure);
                }

                var normalizedDisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
                if (normalizedDisplayName?.Length > 100)
                {
                    await api.Error.WriteLineAsync(
                        "--display-name must be 100 characters or fewer.").ConfigureAwait(false);
                    return CliExitCode.For(CliExitOutcome.OperationFailure);
                }

                var (resolvedRunId, resolveExit) = await ResolveRunTargetAsync(
                    api, runId, issue, project).ConfigureAwait(false);
                if (resolveExit != 0)
                    return resolveExit;

                return await api.PrintMutationResourceAsync(
                    HttpMethod.Post,
                    WorkflowRunPath(resolvedRunId!, "/reject"),
                    new { displayName = normalizedDisplayName, message },
                    RunControlDescriptor,
                    selection,
                    data => api.RenderTableAsync(data, MohistCliApi.TableShape.WorkflowRunDetail));
            }
        });
        return cmd;
    }


    private static Command BuildRetry(MohistCliApi api)
    {
        var cmd = new Command("retry", "Retry the current failure point of a workflow run (restores the manual-retry budget; not for arbitrary stages — use 'rerun --from-stage')");
        var runIdArg = RunIdArg();
        var issueOpt = IssueOption();
        var projectOpt = ProjectOptions();
        var jsonOpt = MohistCliCommands.JsonSelectionOption(RunControlDescriptor);
        cmd.Arguments.Add(runIdArg);
        cmd.Options.Add(issueOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(jsonOpt);
        cmd.SetAction(ctx =>
        {
            var runId = ctx.GetValue(runIdArg);
            var issue = ctx.GetValue(issueOpt);
            var project = ctx.GetValue(projectOpt);
            var jsonProvided = ctx.GetResult(jsonOpt) is not null;
            var json = ctx.GetValue(jsonOpt);
            var selection = JsonSelection.Parse(RunControlDescriptor, jsonProvided, json);
            return ActAsync();

            async Task<int> ActAsync()
            {
                if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
                    return api.WriteJsonSelectionResult(RunControlDescriptor, selection);

                var (resolvedRunId, resolveExit) = await ResolveRunTargetAsync(
                    api, runId, issue, project).ConfigureAwait(false);
                if (resolveExit != 0)
                    return resolveExit;

                return await api.PrintMutationResourceAsync(
                    HttpMethod.Post,
                    WorkflowRunPath(resolvedRunId!, "/retry"),
                    new { },
                    RunControlDescriptor,
                    selection,
                    data => api.RenderTableAsync(data, MohistCliApi.TableShape.WorkflowRunDetail));
            }
        });
        return cmd;
    }


    private static Command BuildRerun(MohistCliApi api)
    {
        var cmd = new Command(
            "rerun",
            "Rerun the entire workflow run (no flag) or from a specific stage (--from-stage). The 'rerun-from-stage' subcommand does not exist — use --from-stage.");
        var runIdArg = RunIdArg();
        var issueOpt = IssueOption();
        var projectOpt = ProjectOptions();
        var fromStageOpt = new Option<string?>("--from-stage")
        {
            Description = "Rerun from the specified stage (invalidates that stage and all later stages)",
        };
        var jsonOpt = MohistCliCommands.JsonSelectionOption(RunControlDescriptor);
        cmd.Arguments.Add(runIdArg);
        cmd.Options.Add(issueOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(fromStageOpt);
        cmd.Options.Add(jsonOpt);
        cmd.SetAction(ctx =>
        {
            var runId = ctx.GetValue(runIdArg);
            var issue = ctx.GetValue(issueOpt);
            var project = ctx.GetValue(projectOpt);
            var fromStage = ctx.GetValue(fromStageOpt);
            var fromStageProvided = ctx.GetResult(fromStageOpt) is not null;
            var jsonProvided = ctx.GetResult(jsonOpt) is not null;
            var json = ctx.GetValue(jsonOpt);
            var selection = JsonSelection.Parse(RunControlDescriptor, jsonProvided, json);
            return RerunAsync();

            async Task<int> RerunAsync()
            {
                if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
                    return api.WriteJsonSelectionResult(RunControlDescriptor, selection);

                if (fromStageProvided && string.IsNullOrWhiteSpace(fromStage))
                {
                    await api.Error.WriteLineAsync(
                        "--from-stage is required and must not be empty").ConfigureAwait(false);
                    return CliExitCode.For(CliExitOutcome.OperationFailure);
                }

                var (resolvedRunId, resolveExit) = await ResolveRunTargetAsync(
                    api, runId, issue, project).ConfigureAwait(false);
                if (resolveExit != 0)
                    return resolveExit;

                var suffix = fromStageProvided ? "/rerun-from-stage" : "/rerun";
                object body = fromStageProvided ? new { stage = fromStage! } : new { };
                return await api.PrintMutationResourceAsync(
                    HttpMethod.Post,
                    WorkflowRunPath(resolvedRunId!, suffix),
                    body,
                    RunControlDescriptor,
                    selection,
                    data => api.RenderTableAsync(data, MohistCliApi.TableShape.WorkflowRunDetail));
            }
        });
        return cmd;
    }


    private static Command BuildPause(MohistCliApi api)
    {
        var cmd = new Command(
            "pause",
            "Pause a workflow run (resumable via 'mo run resume'; reversible — does not require --yes)");
        var runIdArg = RunIdArg();
        var issueOpt = IssueOption();
        var projectOpt = ProjectOptions();
        var jsonOpt = MohistCliCommands.JsonSelectionOption(RunControlDescriptor);
        cmd.Arguments.Add(runIdArg);
        cmd.Options.Add(issueOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(jsonOpt);
        cmd.SetAction(ctx =>
        {
            var runId = ctx.GetValue(runIdArg);
            var issue = ctx.GetValue(issueOpt);
            var project = ctx.GetValue(projectOpt);
            var jsonProvided = ctx.GetResult(jsonOpt) is not null;
            var json = ctx.GetValue(jsonOpt);
            var selection = JsonSelection.Parse(RunControlDescriptor, jsonProvided, json);
            return ActAsync();

            async Task<int> ActAsync()
            {
                if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
                    return api.WriteJsonSelectionResult(RunControlDescriptor, selection);

                var (resolvedRunId, resolveExit) = await ResolveRunTargetAsync(
                    api, runId, issue, project).ConfigureAwait(false);
                if (resolveExit != 0)
                    return resolveExit;

                return await api.PrintMutationResourceAsync(
                    HttpMethod.Post,
                    WorkflowRunPath(resolvedRunId!, "/pause"),
                    new { },
                    RunControlDescriptor,
                    selection,
                    data => api.RenderTableAsync(data, MohistCliApi.TableShape.WorkflowRunDetail));
            }
        });
        return cmd;
    }


    private static Command BuildResume(MohistCliApi api)
    {
        var cmd = new Command("resume", "Resume a paused workflow run");
        var runIdArg = RunIdArg();
        var issueOpt = IssueOption();
        var projectOpt = ProjectOptions();
        var jsonOpt = MohistCliCommands.JsonSelectionOption(RunControlDescriptor);
        cmd.Arguments.Add(runIdArg);
        cmd.Options.Add(issueOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(jsonOpt);
        cmd.SetAction(ctx =>
        {
            var runId = ctx.GetValue(runIdArg);
            var issue = ctx.GetValue(issueOpt);
            var project = ctx.GetValue(projectOpt);
            var jsonProvided = ctx.GetResult(jsonOpt) is not null;
            var json = ctx.GetValue(jsonOpt);
            var selection = JsonSelection.Parse(RunControlDescriptor, jsonProvided, json);
            return ActAsync();

            async Task<int> ActAsync()
            {
                if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
                    return api.WriteJsonSelectionResult(RunControlDescriptor, selection);

                var (resolvedRunId, resolveExit) = await ResolveRunTargetAsync(
                    api, runId, issue, project).ConfigureAwait(false);
                if (resolveExit != 0)
                    return resolveExit;

                return await api.PrintMutationResourceAsync(
                    HttpMethod.Post,
                    WorkflowRunPath(resolvedRunId!, "/resume"),
                    new { },
                    RunControlDescriptor,
                    selection,
                    data => api.RenderTableAsync(data, MohistCliApi.TableShape.WorkflowRunDetail));
            }
        });
        return cmd;
    }


    private static Command BuildStop(MohistCliApi api)
    {
        var cmd = new Command(
            "stop",
            "Stop a workflow run permanently (terminal — cannot be resumed; use 'pause' for a resumable interruption). Requires --yes in non-interactive mode.");
        var runIdArg = RunIdArg();
        var issueOpt = IssueOption();
        var projectOpt = ProjectOptions();
        var yesOpt = new Option<bool>("--yes", "-y")
        {
            Description = "Bypass confirmation (required in non-interactive mode for this irreversible action)",
        };
        var jsonOpt = MohistCliCommands.JsonSelectionOption(RunControlDescriptor);
        cmd.Arguments.Add(runIdArg);
        cmd.Options.Add(issueOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(yesOpt);
        cmd.Options.Add(jsonOpt);
        cmd.SetAction(ctx =>
        {
            var runId = ctx.GetValue(runIdArg);
            var issue = ctx.GetValue(issueOpt);
            var project = ctx.GetValue(projectOpt);
            var yes = ctx.GetValue(yesOpt);
            var jsonProvided = ctx.GetResult(jsonOpt) is not null;
            var json = ctx.GetValue(jsonOpt);
            var selection = JsonSelection.Parse(RunControlDescriptor, jsonProvided, json);
            return StopAsync();

            async Task<int> StopAsync()
            {
                if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
                    return api.WriteJsonSelectionResult(RunControlDescriptor, selection);

                var targetShapeExit = await ValidateRunTargetShapeAsync(
                    api, runId, issue).ConfigureAwait(false);
                if (targetShapeExit != 0)
                    return targetShapeExit;

                if (!yes && !api.Invocation.PromptsEnabled)
                {
                    await api.Error.WriteLineAsync(
                        "--yes is required to confirm this irreversible action.").ConfigureAwait(false);
                    return CliExitCode.For(CliExitOutcome.OperationFailure);
                }

                var (resolvedRunId, resolveExit) = await ResolveRunTargetAsync(
                    api, runId, issue, project).ConfigureAwait(false);
                if (resolveExit != 0)
                    return resolveExit;

                if (!yes)
                {
                    await api.Error.WriteAsync(
                        $"Stop {resolvedRunId} permanently? This cannot be undone. [y/N] ").ConfigureAwait(false);
                    var line = await api.Invocation.Input
                        .ReadLineAsync(api.Invocation.CancellationToken).ConfigureAwait(false);
                    var confirmed = !string.IsNullOrEmpty(line)
                        && line.TrimStart().StartsWith("y", StringComparison.OrdinalIgnoreCase);
                    if (!confirmed)
                    {
                        await api.Error.WriteLineAsync("Aborted.").ConfigureAwait(false);
                        return CliExitCode.For(CliExitOutcome.OperationFailure);
                    }
                }

                return await api.PrintMutationResourceAsync(
                    HttpMethod.Post,
                    WorkflowRunPath(resolvedRunId!, "/stop"),
                    new { },
                    RunControlDescriptor,
                    selection,
                    data => api.RenderTableAsync(data, MohistCliApi.TableShape.WorkflowRunDetail));
            }
        });
        return cmd;
    }

}
