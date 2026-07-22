using System.CommandLine;

namespace Mohist.Cli;

internal static partial class WorkflowCommands
{
    public static Command Build(MohistCliApi api)
    {
        var workflow = new Command("workflow", "Workflow run management");

        workflow.Subcommands.Add(BuildApprove(api));
        workflow.Subcommands.Add(BuildReject(api));
        workflow.Subcommands.Add(BuildRetry(api));
        workflow.Subcommands.Add(BuildRerun(api));
        workflow.Subcommands.Add(BuildResume(api));
        workflow.Subcommands.Add(BuildPause(api));
        workflow.Subcommands.Add(BuildStop(api));

        workflow.Subcommands.Add(BuildGet(api));
        workflow.Subcommands.Add(BuildVariables(api));
        workflow.Subcommands.Add(BuildEvents(api));
        workflow.Subcommands.Add(BuildListSessions(api));

        return workflow;
    }

    private static string WorkflowRunPath(string runId, string suffix = "") =>
        $"/api/workflow-runs/{MohistCliCommands.Escape(runId)}{(suffix.StartsWith('/') ? suffix : (suffix.Length == 0 ? string.Empty : "/" + suffix))}";

    private static Argument<string?> RunIdArg() => new("run-id")
    {
        Description = "Workflow run id",
        Arity = ArgumentArity.ZeroOrOne,
        DefaultValueFactory = _ => null,
    };

    private static Option<string> FromStageOption() =>
        new("--from-stage", "-s")
        {
            Description = "Rerun from the specified stage (equivalent to 'mo issue rerun-from-stage --stage')",
        };

    private static Command BuildApprove(MohistCliApi api)
    {
        var cmd = new Command("approve", "Pass the approval gate for a workflow run (same grain method as 'mo issue approve')");
        var runIdArg = RunIdArg();
        var dryRunOpt = MohistCliCommands.DryRunOption();
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Arguments.Add(runIdArg);
        cmd.Options.Add(dryRunOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var runId = ctx.GetValue(runIdArg);
            var dryRun = ctx.GetValue(dryRunOpt);
            var output = ctx.GetValue(outputOpt);
            return ActAsync();

            async Task<int> ActAsync()
            {
                if (string.IsNullOrWhiteSpace(runId))
                {
                    api.Error.WriteLine("<run-id> is required");
                    return 1;
                }
                if (dryRun)
                {
                    api.Output.WriteLine($"[dry-run] POST {WorkflowRunPath(runId!, "/approve")}");
                    return 0;
                }
                var (mode, exit) = api.ResolveOutputMode(output);
                if (exit != 0) return exit;
                return await api.PrintPostWithOutputAsync(
                    WorkflowRunPath(runId!, "/approve"),
                    new { },
                    mode,
                    nameof(MohistCliApi.TableShape.WorkflowApproval));
            }
        });
        return cmd;
    }

    private static Command BuildReject(MohistCliApi api)
    {
        var cmd = new Command("reject", "Reject a workflow run at its approval gate with a reason (same grain method as 'mo issue reject')");
        var runIdArg = RunIdArg();
        var messageOpt = new Option<string?>("--message", "-m")
        {
            Description = "Reject reason / change request message (required)",
        };
        var dryRunOpt = MohistCliCommands.DryRunOption();
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Arguments.Add(runIdArg);
        cmd.Options.Add(messageOpt);
        cmd.Options.Add(dryRunOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var runId = ctx.GetValue(runIdArg);
            var message = ctx.GetValue(messageOpt);
            var dryRun = ctx.GetValue(dryRunOpt);
            var output = ctx.GetValue(outputOpt);
            return RejectAsync();

            async Task<int> RejectAsync()
            {
                if (string.IsNullOrWhiteSpace(runId))
                {
                    api.Error.WriteLine("<run-id> is required");
                    return 1;
                }
                if (string.IsNullOrWhiteSpace(message))
                {
                    api.Error.WriteLine("--message is required and must not be empty");
                    return 1;
                }
                if (dryRun)
                {
                    api.Output.WriteLine($"[dry-run] POST {WorkflowRunPath(runId!, "/reject")} {{message=<set>}}");
                    return 0;
                }
                var (mode, exit) = api.ResolveOutputMode(output);
                if (exit != 0) return exit;
                return await api.PrintPostWithOutputAsync(
                    WorkflowRunPath(runId!, "/reject"),
                    new { message },
                    mode,
                    nameof(MohistCliApi.TableShape.IssueShow));
            }
        });
        return cmd;
    }

    private static Command BuildRetry(MohistCliApi api)
    {
        var cmd = new Command("retry", "Retry the current stage of a workflow run (same grain method as 'mo issue retry')");
        var runIdArg = RunIdArg();
        var dryRunOpt = MohistCliCommands.DryRunOption();
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Arguments.Add(runIdArg);
        cmd.Options.Add(dryRunOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var runId = ctx.GetValue(runIdArg);
            var dryRun = ctx.GetValue(dryRunOpt);
            var output = ctx.GetValue(outputOpt);
            return ActAsync();

            async Task<int> ActAsync()
            {
                if (string.IsNullOrWhiteSpace(runId))
                {
                    api.Error.WriteLine("<run-id> is required");
                    return 1;
                }
                if (dryRun)
                {
                    api.Output.WriteLine($"[dry-run] POST {WorkflowRunPath(runId!, "/retry")}");
                    return 0;
                }
                var (mode, exit) = api.ResolveOutputMode(output);
                if (exit != 0) return exit;
                return await api.PrintPostWithOutputAsync(
                    WorkflowRunPath(runId!, "/retry"),
                    new { },
                    mode,
                    nameof(MohistCliApi.TableShape.IssueShow));
            }
        });
        return cmd;
    }

    private static Command BuildRerun(MohistCliApi api)
    {
        var cmd = new Command("rerun", "Rerun a workflow run from the start (no flag) or from a specific stage (--from-stage; equivalent to 'mo issue rerun-from-stage --stage')");
        var runIdArg = RunIdArg();
        var fromStageOpt = FromStageOption();
        var dryRunOpt = MohistCliCommands.DryRunOption();
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Arguments.Add(runIdArg);
        cmd.Options.Add(fromStageOpt);
        cmd.Options.Add(dryRunOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var runId = ctx.GetValue(runIdArg);
            var fromStage = ctx.GetValue(fromStageOpt);
            var fromStageProvided = ctx.GetResult(fromStageOpt) is not null;
            var dryRun = ctx.GetValue(dryRunOpt);
            var output = ctx.GetValue(outputOpt);
            return RerunAsync();

            async Task<int> RerunAsync()
            {
                if (string.IsNullOrWhiteSpace(runId))
                {
                    api.Error.WriteLine("<run-id> is required");
                    return 1;
                }
                if (fromStageProvided && string.IsNullOrWhiteSpace(fromStage))
                {
                    api.Error.WriteLine("--from-stage is required and must not be empty");
                    return 1;
                }
                var suffix = fromStageProvided ? "/rerun-from-stage" : "/rerun";
                object body = fromStageProvided ? new { stage = fromStage! } : new { };
                if (dryRun)
                {
                    api.Output.WriteLine($"[dry-run] POST {WorkflowRunPath(runId!, suffix)}{(fromStageProvided ? $" {{stage={fromStage}}}" : string.Empty)}");
                    return 0;
                }
                var (mode, exit) = api.ResolveOutputMode(output);
                if (exit != 0) return exit;
                return await api.PrintPostWithOutputAsync(
                    WorkflowRunPath(runId!, suffix),
                    body,
                    mode,
                    nameof(MohistCliApi.TableShape.IssueShow));
            }
        });
        return cmd;
    }

    private static Command BuildResume(MohistCliApi api)
    {
        var cmd = new Command("resume", "Resume a paused workflow run (same grain method as 'mo issue resume')");
        var runIdArg = RunIdArg();
        var dryRunOpt = MohistCliCommands.DryRunOption();
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Arguments.Add(runIdArg);
        cmd.Options.Add(dryRunOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var runId = ctx.GetValue(runIdArg);
            var dryRun = ctx.GetValue(dryRunOpt);
            var output = ctx.GetValue(outputOpt);
            return ActAsync();

            async Task<int> ActAsync()
            {
                if (string.IsNullOrWhiteSpace(runId))
                {
                    api.Error.WriteLine("<run-id> is required");
                    return 1;
                }
                if (dryRun)
                {
                    api.Output.WriteLine($"[dry-run] POST {WorkflowRunPath(runId!, "/resume")}");
                    return 0;
                }
                var (mode, exit) = api.ResolveOutputMode(output);
                if (exit != 0) return exit;
                return await api.PrintPostWithOutputAsync(
                    WorkflowRunPath(runId!, "/resume"),
                    new { },
                    mode,
                    nameof(MohistCliApi.TableShape.IssueShow));
            }
        });
        return cmd;
    }

    private static Command BuildPause(MohistCliApi api)
    {
        var cmd = new Command("pause", "Pause a workflow run (resumable via 'mo workflow resume'; same grain method as 'mo issue force-stop')");
        var runIdArg = RunIdArg();
        var dryRunOpt = MohistCliCommands.DryRunOption();
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Arguments.Add(runIdArg);
        cmd.Options.Add(dryRunOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var runId = ctx.GetValue(runIdArg);
            var dryRun = ctx.GetValue(dryRunOpt);
            var output = ctx.GetValue(outputOpt);
            return ActAsync();

            async Task<int> ActAsync()
            {
                if (string.IsNullOrWhiteSpace(runId))
                {
                    api.Error.WriteLine("<run-id> is required");
                    return 1;
                }
                if (dryRun)
                {
                    api.Output.WriteLine($"[dry-run] POST {WorkflowRunPath(runId!, "/pause")}");
                    return 0;
                }
                var (mode, exit) = api.ResolveOutputMode(output);
                if (exit != 0) return exit;
                return await api.PrintPostWithOutputAsync(
                    WorkflowRunPath(runId!, "/pause"),
                    new { },
                    mode,
                    nameof(MohistCliApi.TableShape.IssueShow));
            }
        });
        return cmd;
    }

    private static Command BuildStop(MohistCliApi api)
    {
        var cmd = new Command(
            "stop",
            "Stop a workflow run permanently (terminal — cannot be resumed; use 'pause' if you want a pause you can resume; same grain method as 'mo issue stop')");
        var runIdArg = RunIdArg();
        var dryRunOpt = MohistCliCommands.DryRunOption();
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Arguments.Add(runIdArg);
        cmd.Options.Add(dryRunOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var runId = ctx.GetValue(runIdArg);
            var dryRun = ctx.GetValue(dryRunOpt);
            var output = ctx.GetValue(outputOpt);
            return ActAsync();

            async Task<int> ActAsync()
            {
                if (string.IsNullOrWhiteSpace(runId))
                {
                    api.Error.WriteLine("<run-id> is required");
                    return 1;
                }
                if (dryRun)
                {
                    api.Output.WriteLine($"[dry-run] POST {WorkflowRunPath(runId!, "/stop")}");
                    return 0;
                }
                var (mode, exit) = api.ResolveOutputMode(output);
                if (exit != 0) return exit;
                return await api.PrintPostWithOutputAsync(
                    WorkflowRunPath(runId!, "/stop"),
                    new { },
                    mode,
                    nameof(MohistCliApi.TableShape.IssueShow));
            }
        });
        return cmd;
    }
}
