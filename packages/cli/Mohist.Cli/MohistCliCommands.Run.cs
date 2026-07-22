using System.CommandLine;
using System.CommandLine.Parsing;

namespace Mohist.Cli;

// `mo run` is the single command tree for WorkflowRun navigation and
// control. Every state-changing verb (approve / reject / retry / rerun /
// pause / resume / stop) and the run-scoped reads live under it; the
// issue-scoped and legacy `workflow` entry points are removed in T-004.
//
// Target resolution (D2): every command targeting a specific Run accepts
// exactly one of a positional `<run-id>` argument or `--issue <number>`
// (with optional `--project`). Mutual exclusion and missing target are
// enforced locally before any HTTP call. When `--issue` is used the
// resolver reads the issue's bound `workflowRunId` via a one-shot GET
// to `/api/projects/{projectId}/issues/{number}` and fails with a
// diagnostic naming the issue when the issue has no bound run.
//
// `--yes` confirmation (D5): the irreversible `stop` verb requires
// `--yes` in non-interactive contexts (MOHIST_PROMPT_DISABLED=1 or
// redirected stdin). In interactive mode a confirmation prompt is shown;
// `--yes` bypasses it.
internal static partial class RunCommands
{
    private static readonly ResourceDescriptor RunControlDescriptor = new(
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

        // Reads live in `MohistCliCommands.Run.Reads.cs` to keep each
        // partial focused on one concern (control verbs vs. reads vs.
        // feedback — see design D1).
        RegisterReads(run, api);

        return run;
    }

    // ────────────────────────────────────────────────────────────────────
    //  Shared target resolution
    // ────────────────────────────────────────────────────────────────────

    // Resolve the Run ID targeted by a `mo run <verb>` invocation.
    //
    // Inputs:
    //   runId        — positional `<run-id>` argument, or null.
    //   issueNumber  — value of the `--issue` option, or null.
    //   project      — value of the `--project` option, or null. Only used
    //                  when `--issue` is provided.
    //
    // Returns:
    //   (runId, 0)               — caller may proceed with the run-scoped call.
    //   (null, 2)                — usage failure (both or neither provided).
    //                              No HTTP request has been issued.
    //   (null, non-zero)         — operation failure (project resolve, issue
    //                              GET, missing workflowRunId). The error has
    //                              already been written to api.Error.
    //
    // When `runId` is provided the resolver performs no HTTP call and no
    // project resolution — the run-scoped endpoint accepts the run id
    // directly. When `--issue` is provided the resolver performs exactly
    // one HTTP GET to fetch the issue resource and read its
    // `workflowRunId` field.
    internal static async Task<(string? RunId, int Exit)> ResolveRunTargetAsync(
        MohistCliApi api,
        string? runId,
        string? issueNumber,
        string? project)
    {
        var hasRunId = !string.IsNullOrWhiteSpace(runId);
        var hasIssue = !string.IsNullOrWhiteSpace(issueNumber);

        if (hasRunId && hasIssue)
        {
            await api.Error.WriteLineAsync(
                "Provide either a Run ID or --issue, not both.").ConfigureAwait(false);
            return (null, CliExitCode.For(CliExitOutcome.UsageFailure));
        }

        if (!hasRunId && !hasIssue)
        {
            await api.Error.WriteLineAsync(
                "A Run ID or --issue <number> is required.").ConfigureAwait(false);
            return (null, CliExitCode.For(CliExitOutcome.UsageFailure));
        }

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

    // ────────────────────────────────────────────────────────────────────
    //  Shared option / argument factories
    // ────────────────────────────────────────────────────────────────────

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

    private static (Option<string?> Project, Option<string?> ProjectId) ProjectOptions() =>
        MohistCliCommands.ProjectRefOption();

    private static string WorkflowRunPath(string runId, string suffix) =>
        $"/api/workflow-runs/{MohistCliCommands.Escape(runId)}{suffix}";

    // ────────────────────────────────────────────────────────────────────
    //  approve
    // ────────────────────────────────────────────────────────────────────

    private static Command BuildApprove(MohistCliApi api)
    {
        var cmd = new Command("approve", "Pass the approval gate for a workflow run");
        var runIdArg = RunIdArg();
        var issueOpt = IssueOption();
        var (projectOpt, projectIdOpt) = ProjectOptions();
        var jsonOpt = MohistCliCommands.JsonSelectionOption();
        cmd.Arguments.Add(runIdArg);
        cmd.Options.Add(issueOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(jsonOpt);
        cmd.SetAction(ctx =>
        {
            var runId = ctx.GetValue(runIdArg);
            var issue = ctx.GetValue(issueOpt);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var jsonProvided = ctx.GetResult(jsonOpt) is not null;
            var json = ctx.GetValue(jsonOpt);
            var selection = JsonSelection.Parse(RunControlDescriptor, jsonProvided, json);
            return ActAsync();

            async Task<int> ActAsync()
            {
                if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
                    return api.WriteJsonSelectionResult(RunControlDescriptor, selection);

                var (resolvedRunId, resolveExit) = await ResolveRunTargetAsync(
                    api, runId, issue, MergeProject(project, projectId)).ConfigureAwait(false);
                if (resolveExit != 0)
                    return resolveExit;

                return await api.PrintMutationResourceAsync(
                    HttpMethod.Post,
                    WorkflowRunPath(resolvedRunId!, "/approve"),
                    new { },
                    RunControlDescriptor,
                    selection,
                    data => api.RenderTableAsync(data, MohistCliApi.TableShape.WorkflowRunDetail));
            }
        });
        return cmd;
    }

    // ────────────────────────────────────────────────────────────────────
    //  reject — requires --message
    // ────────────────────────────────────────────────────────────────────

    private static Command BuildReject(MohistCliApi api)
    {
        var cmd = new Command(
            "reject",
            "Reject the workflow run at its approval gate with a reason (use --message; required)");
        var runIdArg = RunIdArg();
        var issueOpt = IssueOption();
        var (projectOpt, projectIdOpt) = ProjectOptions();
        var messageOpt = new Option<string?>("--message", "-m")
        {
            Description = "Reject reason / change request message (required, must not be empty)",
        };
        var jsonOpt = MohistCliCommands.JsonSelectionOption();
        cmd.Arguments.Add(runIdArg);
        cmd.Options.Add(issueOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(messageOpt);
        cmd.Options.Add(jsonOpt);
        cmd.SetAction(ctx =>
        {
            var runId = ctx.GetValue(runIdArg);
            var issue = ctx.GetValue(issueOpt);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var message = ctx.GetValue(messageOpt);
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

                var (resolvedRunId, resolveExit) = await ResolveRunTargetAsync(
                    api, runId, issue, MergeProject(project, projectId)).ConfigureAwait(false);
                if (resolveExit != 0)
                    return resolveExit;

                return await api.PrintMutationResourceAsync(
                    HttpMethod.Post,
                    WorkflowRunPath(resolvedRunId!, "/reject"),
                    new { message },
                    RunControlDescriptor,
                    selection,
                    data => api.RenderTableAsync(data, MohistCliApi.TableShape.WorkflowRunDetail));
            }
        });
        return cmd;
    }

    // ────────────────────────────────────────────────────────────────────
    //  retry
    // ────────────────────────────────────────────────────────────────────

    private static Command BuildRetry(MohistCliApi api)
    {
        var cmd = new Command("retry", "Retry the current failure point of a workflow run (restores the manual-retry budget; not for arbitrary stages — use 'rerun --from-stage')");
        var runIdArg = RunIdArg();
        var issueOpt = IssueOption();
        var (projectOpt, projectIdOpt) = ProjectOptions();
        var jsonOpt = MohistCliCommands.JsonSelectionOption();
        cmd.Arguments.Add(runIdArg);
        cmd.Options.Add(issueOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(jsonOpt);
        cmd.SetAction(ctx =>
        {
            var runId = ctx.GetValue(runIdArg);
            var issue = ctx.GetValue(issueOpt);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var jsonProvided = ctx.GetResult(jsonOpt) is not null;
            var json = ctx.GetValue(jsonOpt);
            var selection = JsonSelection.Parse(RunControlDescriptor, jsonProvided, json);
            return ActAsync();

            async Task<int> ActAsync()
            {
                if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
                    return api.WriteJsonSelectionResult(RunControlDescriptor, selection);

                var (resolvedRunId, resolveExit) = await ResolveRunTargetAsync(
                    api, runId, issue, MergeProject(project, projectId)).ConfigureAwait(false);
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

    // ────────────────────────────────────────────────────────────────────
    //  rerun (with --from-stage)
    // ────────────────────────────────────────────────────────────────────

    private static Command BuildRerun(MohistCliApi api)
    {
        var cmd = new Command(
            "rerun",
            "Rerun the entire workflow run (no flag) or from a specific stage (--from-stage). The 'rerun-from-stage' subcommand does not exist — use --from-stage.");
        var runIdArg = RunIdArg();
        var issueOpt = IssueOption();
        var (projectOpt, projectIdOpt) = ProjectOptions();
        var fromStageOpt = new Option<string?>("--from-stage", "-s")
        {
            Description = "Rerun from the specified stage (invalidates that stage and all later stages)",
        };
        var jsonOpt = MohistCliCommands.JsonSelectionOption();
        cmd.Arguments.Add(runIdArg);
        cmd.Options.Add(issueOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(fromStageOpt);
        cmd.Options.Add(jsonOpt);
        cmd.SetAction(ctx =>
        {
            var runId = ctx.GetValue(runIdArg);
            var issue = ctx.GetValue(issueOpt);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
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
                    api, runId, issue, MergeProject(project, projectId)).ConfigureAwait(false);
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

    // ────────────────────────────────────────────────────────────────────
    //  pause — reversible, no confirmation
    // ────────────────────────────────────────────────────────────────────

    private static Command BuildPause(MohistCliApi api)
    {
        var cmd = new Command(
            "pause",
            "Pause a workflow run (resumable via 'mo run resume'; reversible — does not require --yes)");
        var runIdArg = RunIdArg();
        var issueOpt = IssueOption();
        var (projectOpt, projectIdOpt) = ProjectOptions();
        var jsonOpt = MohistCliCommands.JsonSelectionOption();
        cmd.Arguments.Add(runIdArg);
        cmd.Options.Add(issueOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(jsonOpt);
        cmd.SetAction(ctx =>
        {
            var runId = ctx.GetValue(runIdArg);
            var issue = ctx.GetValue(issueOpt);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var jsonProvided = ctx.GetResult(jsonOpt) is not null;
            var json = ctx.GetValue(jsonOpt);
            var selection = JsonSelection.Parse(RunControlDescriptor, jsonProvided, json);
            return ActAsync();

            async Task<int> ActAsync()
            {
                if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
                    return api.WriteJsonSelectionResult(RunControlDescriptor, selection);

                var (resolvedRunId, resolveExit) = await ResolveRunTargetAsync(
                    api, runId, issue, MergeProject(project, projectId)).ConfigureAwait(false);
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

    // ────────────────────────────────────────────────────────────────────
    //  resume
    // ────────────────────────────────────────────────────────────────────

    private static Command BuildResume(MohistCliApi api)
    {
        var cmd = new Command("resume", "Resume a paused workflow run");
        var runIdArg = RunIdArg();
        var issueOpt = IssueOption();
        var (projectOpt, projectIdOpt) = ProjectOptions();
        var jsonOpt = MohistCliCommands.JsonSelectionOption();
        cmd.Arguments.Add(runIdArg);
        cmd.Options.Add(issueOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(jsonOpt);
        cmd.SetAction(ctx =>
        {
            var runId = ctx.GetValue(runIdArg);
            var issue = ctx.GetValue(issueOpt);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var jsonProvided = ctx.GetResult(jsonOpt) is not null;
            var json = ctx.GetValue(jsonOpt);
            var selection = JsonSelection.Parse(RunControlDescriptor, jsonProvided, json);
            return ActAsync();

            async Task<int> ActAsync()
            {
                if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
                    return api.WriteJsonSelectionResult(RunControlDescriptor, selection);

                var (resolvedRunId, resolveExit) = await ResolveRunTargetAsync(
                    api, runId, issue, MergeProject(project, projectId)).ConfigureAwait(false);
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

    // ────────────────────────────────────────────────────────────────────
    //  stop — irreversible, requires --yes in non-interactive mode
    // ────────────────────────────────────────────────────────────────────

    private static Command BuildStop(MohistCliApi api)
    {
        var cmd = new Command(
            "stop",
            "Stop a workflow run permanently (terminal — cannot be resumed; use 'pause' for a resumable interruption). Requires --yes in non-interactive mode.");
        var runIdArg = RunIdArg();
        var issueOpt = IssueOption();
        var (projectOpt, projectIdOpt) = ProjectOptions();
        var yesOpt = new Option<bool>("--yes", "-y")
        {
            Description = "Bypass confirmation (required in non-interactive mode for this irreversible action)",
        };
        var jsonOpt = MohistCliCommands.JsonSelectionOption();
        cmd.Arguments.Add(runIdArg);
        cmd.Options.Add(issueOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(yesOpt);
        cmd.Options.Add(jsonOpt);
        cmd.SetAction(ctx =>
        {
            var runId = ctx.GetValue(runIdArg);
            var issue = ctx.GetValue(issueOpt);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var yes = ctx.GetValue(yesOpt);
            var jsonProvided = ctx.GetResult(jsonOpt) is not null;
            var json = ctx.GetValue(jsonOpt);
            var selection = JsonSelection.Parse(RunControlDescriptor, jsonProvided, json);
            return StopAsync();

            async Task<int> StopAsync()
            {
                if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
                    return api.WriteJsonSelectionResult(RunControlDescriptor, selection);

                var (resolvedRunId, resolveExit) = await ResolveRunTargetAsync(
                    api, runId, issue, MergeProject(project, projectId)).ConfigureAwait(false);
                if (resolveExit != 0)
                    return resolveExit;

                if (!yes)
                {
                    if (!api.Invocation.PromptsEnabled)
                    {
                        await api.Error.WriteLineAsync(
                            "--yes is required to confirm this irreversible action.").ConfigureAwait(false);
                        return CliExitCode.For(CliExitOutcome.OperationFailure);
                    }

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

    // Pick whichever of --project / --project-id was supplied (issue #475
    // contract: --project is the canonical option, --project-id is the
    // hidden legacy alias). When both are supplied the resolver itself
    // surfaces the mismatch error.
    private static string? MergeProject(string? project, string? projectId) =>
        !string.IsNullOrWhiteSpace(project) ? project : projectId;
}