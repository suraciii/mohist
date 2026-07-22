using System.CommandLine;
using System.Text.Json.Nodes;

namespace Mohist.Cli;

// `mo run` reads — list / view / watch. These sit alongside the control
// verbs in `MohistCliCommands.Run.cs` and share the same target-resolution
// contract (Run ID or `--issue <number>`, never both). The reads cover:
//
//   * list   — derived from `GET /api/projects/{projectId}/issues`,
//              filtered to issues with a non-null `workflowRunId` and
//              projected to { id, status, stage, currentStage, issueNumber }.
//              No dedicated run-collection endpoint exists today (design D3),
//              so the derivation is the single source of truth for the CLI.
//   * view   — `GET /api/workflow-runs/{id}` for the full detail (status,
//              stages, approval state, associated issue). `--yaml` flips to
//              `GET /api/workflow-runs/{id}/yaml` and prints the rendered
//              Workflow Definition; `--yaml` and `--json` are mutually
//              exclusive so neither runs HTTP when both are supplied.
//   * watch  — polling loop on the run detail endpoint. NDJSON lines are
//              printed only when status / stage changes between polls;
//              the loop exits with 0 when the run reaches a terminal status
//              (completed / stopped / cancelled) and with 130 when the user
//              interrupts (Ctrl-C). The poll interval is injectable via
//              `MohistCliApi.TimeProvider` so tests use `FakeTimeProvider`
//              without wall-clock dependencies (design D4 + testing.md).
internal static partial class RunCommands
{
    // Default poll interval for `mo run watch` — overridable via the
    // `--interval` flag. Held as a small constant so tests can pin the
    // exact delay they advance the FakeTimeProvider by.
    internal static readonly TimeSpan DefaultWatchInterval = TimeSpan.FromSeconds(2);

    // Statuses that terminate `mo run watch`. Anything outside this set
    // keeps the polling loop running.
    private static readonly HashSet<string> TerminalRunStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "completed",
        "succeeded",
        "stopped",
        "cancelled",
        "canceled",
        "failed",
    };

    private static readonly ResourceDescriptor RunListDescriptor = new(
        ResourceCardinality.Collection,
        ["id", "status", "stage", "currentStage", "issueNumber"]);

    private static readonly ResourceDescriptor RunViewDescriptor = new(
        ResourceCardinality.Single,
        ["id", "status", "currentStage", "stages", "issueRef"]);

    public static void RegisterReads(Command runCommand, MohistCliApi api)
    {
        runCommand.Subcommands.Add(BuildList(api));
        runCommand.Subcommands.Add(BuildView(api));
        runCommand.Subcommands.Add(BuildWatch(api));
    }

    // ────────────────────────────────────────────────────────────────────
    //  list — derived from the project issues list (design D3)
    // ────────────────────────────────────────────────────────────────────

    private static Command BuildList(MohistCliApi api)
    {
        var cmd = new Command(
            "list",
            "List workflow runs visible in the current project scope. Derived from the project issues list (each issue with a bound run contributes one row).");
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var jsonOpt = MohistCliCommands.JsonSelectionOption();
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(jsonOpt);
        cmd.SetAction(ctx =>
        {
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var json = ctx.GetValue(jsonOpt);
            var jsonProvided = ctx.GetResult(jsonOpt) is not null;
            return ListAsync();

            async Task<int> ListAsync()
            {
                var selection = JsonSelection.Parse(RunListDescriptor, jsonProvided, json);
                if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
                    return api.WriteJsonSelectionResult(RunListDescriptor, selection);

                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId).ConfigureAwait(false);
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

    // Derive the run-list collection from the issues list response: keep
    // only issues with a non-null `workflowRunId`, then project each to
    // { id, status, stage, currentStage, issueNumber }. The mapping matches
    // the WorkflowRunDetailDto vocabulary (id / status / currentStage) plus
    // the convenience aliases (stage, issueNumber) used by the table view
    // and the `--json` field-selection contract.
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

    // ────────────────────────────────────────────────────────────────────
    //  view — full WorkflowRunDetail with --yaml and --json
    // ────────────────────────────────────────────────────────────────────

    private static Command BuildView(MohistCliApi api)
    {
        var cmd = new Command(
            "view",
            "Show full workflow run resource (status, stages, approval state, associated issue). Use --yaml to print the Workflow Definition; --yaml and --json are mutually exclusive.");
        var runIdArg = RunIdArg();
        var issueOpt = IssueOption();
        var (projectOpt, projectIdOpt) = ProjectOptions();
        var yamlOpt = new Option<bool>("--yaml")
        {
            Description = "Print the Workflow Definition YAML source (mutually exclusive with --json)",
        };
        var jsonOpt = MohistCliCommands.JsonSelectionOption();
        cmd.Arguments.Add(runIdArg);
        cmd.Options.Add(issueOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(yamlOpt);
        cmd.Options.Add(jsonOpt);
        cmd.SetAction(ctx =>
        {
            var runId = ctx.GetValue(runIdArg);
            var issue = ctx.GetValue(issueOpt);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var yaml = ctx.GetValue(yamlOpt);
            var jsonProvided = ctx.GetResult(jsonOpt) is not null;
            var json = ctx.GetValue(jsonOpt);
            var selection = JsonSelection.Parse(RunViewDescriptor, jsonProvided, json);
            return ViewAsync();

            async Task<int> ViewAsync()
            {
                if (yaml && jsonProvided)
                {
                    await api.Error.WriteLineAsync(
                        "--yaml and --json cannot be used together").ConfigureAwait(false);
                    return CliExitCode.For(CliExitOutcome.UsageFailure);
                }

                if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
                    return api.WriteJsonSelectionResult(RunViewDescriptor, selection);

                var (resolvedRunId, resolveExit) = await ResolveRunTargetAsync(
                    api, runId, issue, MergeProject(project, projectId)).ConfigureAwait(false);
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
            ["issueRef"] = data?["issueRef"]?.DeepClone(),
        };
    }

    // Fetches `GET /api/workflow-runs/{id}/yaml` and prints the rendered
    // template-definition YAML. Mirrors the legacy `mo workflow get --yaml`
    // path that `mo run view --yaml` replaces (issue-476 D4/D9). Errors
    // bubble up through `GetDataOrPrintErrorAsync` so server failures are
    // surfaced verbatim on stderr with a non-zero exit.
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

    // ────────────────────────────────────────────────────────────────────
    //  watch — polling loop, NDJSON on change, exit on terminal
    // ────────────────────────────────────────────────────────────────────

    private static Command BuildWatch(MohistCliApi api)
    {
        var cmd = new Command(
            "watch",
            "Follow a workflow run's progress. Prints a JSON status line whenever status or stage changes and exits 0 when the run reaches a terminal state (completed / stopped / cancelled) or 130 when interrupted.");
        var runIdArg = RunIdArg();
        var issueOpt = IssueOption();
        var (projectOpt, projectIdOpt) = ProjectOptions();
        var intervalOpt = new Option<int?>("--interval", "-i")
        {
            Description = "Poll interval in milliseconds (default: 2000)",
        };
        cmd.Arguments.Add(runIdArg);
        cmd.Options.Add(issueOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(intervalOpt);
        cmd.SetAction(ctx =>
        {
            var runId = ctx.GetValue(runIdArg);
            var issue = ctx.GetValue(issueOpt);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var intervalMs = ctx.GetValue(intervalOpt);
            return WatchAsync();

            async Task<int> WatchAsync()
            {
                var (resolvedRunId, resolveExit) = await ResolveRunTargetAsync(
                    api, runId, issue, MergeProject(project, projectId)).ConfigureAwait(false);
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

    // Polls the run detail endpoint until the run reaches a terminal
    // status. Each poll captures `{ id, status, stage }` and writes a
    // single NDJSON line to stdout when the captured value changes from
    // the previous one (the first poll is always emitted). The delay
    // between polls is delegated to the injected TimeProvider so tests
    // can advance a FakeTimeProvider instead of waiting on wall-clock.
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

    // One poll: GET /api/workflow-runs/{id}, read the status / stage /
    // currentStage fields, and capture them for the change-detection
    // layer. Server errors are surfaced via `GetDataOrPrintErrorAsync`,
    // which already prints the message + code on stderr and returns a
    // non-zero exit; we propagate that exit code verbatim so the loop
    // aborts without further HTTP traffic.
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
        var snapshot = new WatchSnapshot(runId, statusText, stageText);
        return (0, snapshot);
    }

    // One NDJSON line per change. Compact (single-line) output keeps
    // each line easy to parse and stable in `mo run watch | jq`.
    private sealed record WatchSnapshot(string Id, string? Status, string? Stage)
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
            return node.ToJsonString(MohistCliApi.JsonCompactOutputOptions);
        }
    }
}
