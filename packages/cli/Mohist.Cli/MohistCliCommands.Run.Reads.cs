using System.CommandLine;
using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal static partial class RunCommands
{
    internal static readonly TimeSpan DefaultWatchInterval = TimeSpan.FromSeconds(2);
    private static readonly HashSet<string> TerminalRunStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "completed",
        "succeeded",
        "stopped",
        "cancelled",
        "canceled",
        "failed",
    };

    internal static readonly ResourceDescriptor RunListDescriptor = new(
        ResourceCardinality.Collection,
        ["id", "status", "stage", "currentStage", "issueNumber"]);

    internal static readonly ResourceDescriptor RunViewDescriptor = new(
        ResourceCardinality.Single,
        ["id", "status", "currentStage", "stages", "agentResultAttention", "issueRef", "agentAction", "agentRuntime"]);

    public static void RegisterReads(Command runCommand, MohistCliApi api)
    {
        runCommand.Subcommands.Add(BuildList(api));
        runCommand.Subcommands.Add(BuildView(api));
        runCommand.Subcommands.Add(BuildWatch(api));
    }


    private static Command BuildList(MohistCliApi api)
    {
        var cmd = new Command(
            "list",
            "List workflow runs visible in the current project scope. Derived from the project issues list (each issue with a bound run contributes one row).");
        var projectOpt = MohistCliCommands.ProjectRefOption();
        var jsonOpt = MohistCliCommands.JsonSelectionOption(RunListDescriptor);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(jsonOpt);
        cmd.SetAction(ctx =>
        {
            var project = ctx.GetValue(projectOpt);
            var json = ctx.GetValue(jsonOpt);
            var jsonProvided = ctx.GetResult(jsonOpt) is not null;
            return ListAsync();

            async Task<int> ListAsync()
            {
                var selection = JsonSelection.Parse(RunListDescriptor, jsonProvided, json);
                if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
                    return api.WriteJsonSelectionResult(RunListDescriptor, selection);

                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project).ConfigureAwait(false);
                if (resolveExit != 0)
                    return resolveExit;

                var path = $"/api/projects/{MohistCliCommands.Escape(resolvedProjectId)}/issues";
                var (dataExit, issuesData) = await api.GetDataOrPrintErrorAsync(path).ConfigureAwait(false);
                if (dataExit != 0)
                    return dataExit;

                var projected = ProjectRunsFromIssues(issuesData);

                if (selection.Kind == JsonSelectionKind.Selected)
                {
                    return await new CliResultWriter(api.Invocation)
                        .WriteSuccessAsync(
                            selection.Project(projected, RunListDescriptor.Cardinality))
                        .ConfigureAwait(false);
                }

                return await api.RenderTableAsync(projected, MohistCliApi.TableShape.RunList).ConfigureAwait(false);
            }
        });
        return cmd;
    }

    private static JsonArray ProjectRunsFromIssues(JsonNode? issuesData)
    {
        var result = new JsonArray();
        if (issuesData is not JsonArray issues)
            return result;

        foreach (var node in issues)
        {
            if (node is not JsonObject issue) continue;

            var runId = issue["workflowRunId"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(runId)) continue;

            // Prefer `workflowStatus` (the run's status, kept in sync with
            // WorkflowStatusView.Status). Fall back to the issue status only
            // for legacy issues where the projection hasn't run.
            var status = issue["workflowStatus"]?.GetValue<string>()
                ?? issue["status"]?.GetValue<string>();
            var stage = issue["workflowStage"]?.GetValue<string>();

            var projected = new JsonObject
            {
                ["id"] = runId,
                ["status"] = status,
                ["stage"] = stage,
                ["currentStage"] = stage,
                ["issueNumber"] = issue["number"]?.DeepClone(),
            };
            result.Add(projected);
        }

        return result;
    }


    private static Command BuildView(MohistCliApi api)
    {
        var cmd = new Command(
            "view",
            "Show full workflow run resource (status, stages, approval state, associated issue). Use --yaml to print the Workflow Definition; --yaml and --json are mutually exclusive.");
        var runIdArg = RunIdArg();
        var issueOpt = IssueOption();
        var projectOpt = ProjectOptions();
        var yamlOpt = new Option<bool>("--yaml")
        {
            Description = "Print the Workflow Definition YAML source (mutually exclusive with --json)",
        };
        var jsonOpt = MohistCliCommands.JsonSelectionOption(RunViewDescriptor);
        cmd.Arguments.Add(runIdArg);
        cmd.Options.Add(issueOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(yamlOpt);
        cmd.Options.Add(jsonOpt);
        cmd.Validators.Add(result =>
        {
            if (result.GetResult(yamlOpt) is not null && result.GetResult(jsonOpt) is not null)
                result.AddError("--yaml and --json are mutually exclusive.");
        });
        cmd.SetAction(ctx =>
        {
            var runId = ctx.GetValue(runIdArg);
            var issue = ctx.GetValue(issueOpt);
            var project = ctx.GetValue(projectOpt);
            var yaml = ctx.GetValue(yamlOpt);
            var jsonProvided = ctx.GetResult(jsonOpt) is not null;
            var json = ctx.GetValue(jsonOpt);
            var selection = JsonSelection.Parse(RunViewDescriptor, jsonProvided, json);
            return ViewAsync();

            async Task<int> ViewAsync()
            {
                if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
                    return api.WriteJsonSelectionResult(RunViewDescriptor, selection);

                var (resolvedRunId, resolveExit) = await ResolveRunTargetAsync(
                    api, runId, issue, project).ConfigureAwait(false);
                if (resolveExit != 0)
                    return resolveExit;

                if (yaml)
                    return await PrintRunYamlAsync(api, resolvedRunId!).ConfigureAwait(false);

                var (dataExit, data) = await api.GetDataOrPrintErrorAsync(
                    WorkflowRunPath(resolvedRunId!, "")).ConfigureAwait(false);
                if (dataExit != 0)
                    return dataExit;

                if (selection.Kind == JsonSelectionKind.Selected)
                {
                    var projected = selection.Project(
                        ProjectRunViewData(data), RunViewDescriptor.Cardinality);
                    return await new CliResultWriter(api.Invocation)
                        .WriteSuccessAsync(projected).ConfigureAwait(false);
                }

                return await api.RenderTableAsync(
                    data, MohistCliApi.TableShape.WorkflowRunDetail).ConfigureAwait(false);
            }
        });
        return cmd;
    }

    private static JsonObject ProjectRunViewData(JsonNode? data)
    {
        var status = data?["status"] as JsonObject;
        return new JsonObject
        {
            ["id"] = status?["workflowRunId"]?.DeepClone(),
            ["status"] = status?["status"]?.DeepClone(),
            ["currentStage"] = status?["currentStage"]?.DeepClone(),
            ["stages"] = status?["stages"]?.DeepClone(),
            ["agentResultAttention"] = status?["agentResultAttention"]?.DeepClone(),
            ["issueRef"] = data?["issueRef"]?.DeepClone(),
            ["agentAction"] = data?["agentAction"]?.DeepClone(),
            ["agentRuntime"] = data?["agentRuntime"]?.DeepClone(),
        };
    }

    private static async Task<int> PrintRunYamlAsync(MohistCliApi api, string runId)
    {
        var (exitCode, data) = await api.GetDataOrPrintErrorAsync(
            WorkflowRunPath(runId, "/yaml")).ConfigureAwait(false);
        if (exitCode != 0)
            return exitCode;
        if (data is null)
            return 1;

        var yaml = data["yaml"] as JsonValue;
        if (yaml is not null && yaml.TryGetValue<string>(out var text) && !string.IsNullOrEmpty(text))
        {
            await api.Output.WriteLineAsync(text).ConfigureAwait(false);
            return 0;
        }

        await api.Error.WriteLineAsync("Workflow definition not found").ConfigureAwait(false);
        return 1;
    }


    private static Command BuildWatch(MohistCliApi api)
    {
        var cmd = new Command(
            "watch",
            "Follow a workflow run's progress. Prints a JSON status line whenever status or stage changes and exits 0 when the run reaches a terminal state (completed / stopped / cancelled) or 130 when interrupted.");
        var runIdArg = RunIdArg();
        var issueOpt = IssueOption();
        var projectOpt = ProjectOptions();
        var intervalOpt = new Option<int?>("--interval")
        {
            Description = "Poll interval in milliseconds (default: 2000)",
        };
        cmd.Arguments.Add(runIdArg);
        cmd.Options.Add(issueOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(intervalOpt);
        cmd.SetAction(ctx =>
        {
            var runId = ctx.GetValue(runIdArg);
            var issue = ctx.GetValue(issueOpt);
            var project = ctx.GetValue(projectOpt);
            var intervalMs = ctx.GetValue(intervalOpt);
            return WatchAsync();

            async Task<int> WatchAsync()
            {
                var (resolvedRunId, resolveExit) = await ResolveRunTargetAsync(
                    api, runId, issue, project).ConfigureAwait(false);
                if (resolveExit != 0)
                    return resolveExit;

                var interval = ResolveWatchInterval(intervalMs);
                var token = api.Invocation.CancellationToken;
                return await WatchLoopAsync(api, resolvedRunId!, interval, token).ConfigureAwait(false);
            }
        });
        return cmd;
    }

    private static TimeSpan ResolveWatchInterval(int? intervalMs)
    {
        if (intervalMs is null)
            return DefaultWatchInterval;
        if (intervalMs.Value <= 0)
            return DefaultWatchInterval;
        return TimeSpan.FromMilliseconds(intervalMs.Value);
    }

    private static async Task<int> WatchLoopAsync(
        MohistCliApi api,
        string runId,
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        WatchSnapshot? previous = null;
        var timeProvider = api.TimeProvider;

        while (!cancellationToken.IsCancellationRequested)
        {
            var (exitCode, snapshot) = await ReadWatchSnapshotAsync(api, runId, cancellationToken).ConfigureAwait(false);
            if (exitCode != 0)
                return exitCode;
            if (snapshot is null)
                return 1;

            if (!snapshot.Equals(previous))
            {
                await api.Output.WriteLineAsync(snapshot.ToNdjson()).ConfigureAwait(false);
                previous = snapshot;
            }

            if (snapshot.IsTerminal)
                return CliExitCode.For(CliExitOutcome.Success);

            try
            {
                await Task.Delay(interval, timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return CliExitCode.For(CliExitOutcome.Cancelled);
            }
        }

        return CliExitCode.For(CliExitOutcome.Cancelled);
    }

    private static async Task<(int ExitCode, WatchSnapshot? Snapshot)> ReadWatchSnapshotAsync(
        MohistCliApi api,
        string runId,
        CancellationToken cancellationToken)
    {
        var (exitCode, data) = await api.GetDataOrPrintErrorAsync(
            WorkflowRunPath(runId, "")).ConfigureAwait(false);
        if (exitCode != 0)
            return (exitCode, null);
        if (data is null)
            return (1, null);

        var status = data["status"] as JsonObject;
        var statusText = status?["status"]?.GetValue<string>();
        var stageText = status?["currentStage"]?.GetValue<string>();
        var interruption = status?["interruption"] as JsonObject;
        var reason = interruption?["reasonCode"]?.GetValue<string>();
        var deadline = interruption?["recoveryDeadlineAt"]?.GetValue<string>();
        var snapshot = new WatchSnapshot(runId, statusText, stageText, reason, deadline);
        return (0, snapshot);
    }

    private sealed record WatchSnapshot(
        string Id,
        string? Status,
        string? Stage,
        string? InterruptionReason,
        string? RecoveryDeadlineAt)
    {
        public bool IsTerminal => !string.IsNullOrEmpty(Status) && TerminalRunStatuses.Contains(Status);

        public string ToNdjson()
        {
            var node = new JsonObject
            {
                ["id"] = Id,
                ["status"] = Status,
                ["stage"] = Stage,
            };
            if (!string.IsNullOrWhiteSpace(InterruptionReason))
                node["interruptionReason"] = InterruptionReason;
            if (!string.IsNullOrWhiteSpace(RecoveryDeadlineAt))
                node["recoveryDeadlineAt"] = RecoveryDeadlineAt;
            return node.ToJsonString(MohistCliApi.JsonCompactOutputOptions);
        }
    }
}
