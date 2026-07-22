using System.CommandLine;
using System.Text.Json.Nodes;

namespace Mohist.Cli;

// Read commands under `mo workflow`. Each command targets a WorkflowRun
// directly by `workflowRunId` and never asks for an issue number or project
// ref — those concerns belong to the issue-scoped shortcuts under
// `mo issue ...`. The four reads follow the strict three-way distinction
// (output-format / subresource / associated-resource) recorded in
// design/cli.md:
//
//   * get     — full resource; -o yaml renders the workflow template
//                definition by hitting GET .../yaml (no `mo workflow yaml`
//                command — output format never creates a command).
//                `show` is kept as a transitional name alias of `get` so
//                scripts written against the previously-landed surface
//                (see #381 / issue-386 D5) keep working. The alias shares
//                the same handler by construction and cannot diverge.
//   * variables — subresource (own resource path: .../variables/effective);
//                 NOT reachable via `-o` on `get` — it has its own command.
//   * events    — associated resource (read-only CloudEvent stream) with
//                 --limit to bound the result.
//   * list-sessions — associated resource (sessions list only); single
//                 session sub-actions stay under `mo issue session ...`.
//
// (The `status` command once rendered a strict compact subset of the same
// payload `get` reads; it was removed because `get`'s default table output
// already is the summary view — see workflow-run-reads spec.)
internal static partial class WorkflowCommands
{
    private static Command BuildGet(MohistCliApi api)
    {
        var cmd = new Command(
            "get",
                "Show full workflow run resource (status, stages, approval state, associated issue). Use bare --json to discover fields or --json <fields> to select them.");
        cmd.Aliases.Add("show");
        var runIdArg = RunIdArg();
        var outputOpt = MohistCliCommands.OutputOption(defaultValue: "table");
        cmd.Arguments.Add(runIdArg);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var runId = ctx.GetValue(runIdArg);
            var output = ctx.GetValue(outputOpt);
            return GetAsync();

            async Task<int> GetAsync()
            {
                if (string.IsNullOrWhiteSpace(runId))
                {
                    api.Error.WriteLine("<run-id> is required");
                    return 1;
                }

                var (mode, exit) = api.ResolveOutputMode(output);
                if (exit != 0) return exit;
                return await api.PrintWithOutputAsync(
                    WorkflowRunPath(runId!, ""),
                    mode,
                    nameof(MohistCliApi.TableShape.WorkflowRunDetail));
            }
        });
        return cmd;
    }

    private static Command BuildVariables(MohistCliApi api)
    {
        var cmd = new Command(
            "variables",
            "Show effective variables for the run. --stage scopes resolution to a stage; --key returns the value at a dotted key path (a true subresource, not an output format).");
        var runIdArg = RunIdArg();
        var stageOpt = MohistCliCommands.StageOption();
        var keyOpt = new Option<string?>("--key")
        {
            Description = "Dotted key path within effective variables (e.g. some.nested.key). Returns only the value at that path.",
        };
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Arguments.Add(runIdArg);
        cmd.Options.Add(stageOpt);
        cmd.Options.Add(keyOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var runId = ctx.GetValue(runIdArg);
            var stage = ctx.GetValue(stageOpt);
            var key = ctx.GetValue(keyOpt);
            var output = ctx.GetValue(outputOpt);
            return VariablesAsync();

            async Task<int> VariablesAsync()
            {
                if (string.IsNullOrWhiteSpace(runId))
                {
                    api.Error.WriteLine("<run-id> is required");
                    return 1;
                }

                string path;
                if (!string.IsNullOrWhiteSpace(key))
                {
                    var escapedKey = Uri.EscapeDataString(key!);
                    path = WorkflowRunPath(
                        runId!,
                        $"/variables/effective/{escapedKey}");
                }
                else
                {
                    path = WorkflowRunPath(runId!, "/variables/effective");
                }

                if (!string.IsNullOrWhiteSpace(stage))
                {
                    path += $"?stage={MohistCliCommands.Escape(stage!)}";
                }

                var (mode, exit) = api.ResolveOutputMode(output);
                if (exit != 0) return exit;
                return await api.PrintWithOutputAsync(
                    path,
                    mode,
                    nameof(MohistCliApi.TableShape.WorkflowRunVariables));
            }
        });
        return cmd;
    }

    private static Command BuildEvents(MohistCliApi api)
    {
        var cmd = new Command(
            "events",
            "Show the CloudEvent stream associated with the run (an associated resource, read-only). Use --limit to bound the number of events returned.");
        var runIdArg = RunIdArg();
        var limitOpt = new Option<int?>("--limit")
        {
            Description = "Maximum number of events to return (most recent N)",
        };
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Arguments.Add(runIdArg);
        cmd.Options.Add(limitOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var runId = ctx.GetValue(runIdArg);
            var limit = ctx.GetValue(limitOpt);
            var output = ctx.GetValue(outputOpt);
            return EventsAsync();

            async Task<int> EventsAsync()
            {
                if (string.IsNullOrWhiteSpace(runId))
                {
                    api.Error.WriteLine("<run-id> is required");
                    return 1;
                }

                var path = WorkflowRunPath(runId!, "/events");
                if (limit.HasValue)
                {
                    path += $"?limit={limit.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
                }

                var (mode, exit) = api.ResolveOutputMode(output);
                if (exit != 0) return exit;
                return await api.PrintWithOutputAsync(
                    path,
                    mode,
                    nameof(MohistCliApi.TableShape.WorkflowRunEvents));
            }
        });
        return cmd;
    }

    private static Command BuildListSessions(MohistCliApi api)
    {
        var cmd = new Command(
            "list-sessions",
            "List agent sessions associated with the run (associated resource, list only). Single session sub-actions remain under 'mo issue session ...'.");
        var runIdArg = RunIdArg();
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Arguments.Add(runIdArg);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var runId = ctx.GetValue(runIdArg);
            var output = ctx.GetValue(outputOpt);
            return SessionsAsync();

            async Task<int> SessionsAsync()
            {
                if (string.IsNullOrWhiteSpace(runId))
                {
                    api.Error.WriteLine("<run-id> is required");
                    return 1;
                }

                var (mode, exit) = api.ResolveOutputMode(output);
                if (exit != 0) return exit;
                return await api.PrintWithOutputAsync(
                    WorkflowRunPath(runId!, "/sessions"),
                    mode,
                    nameof(MohistCliApi.TableShape.Sessions));
            }
        });
        return cmd;
    }

    // Fetches GET .../yaml and prints the rendered template-definition YAML
    // for the run. Errors (unknown run id, missing definition) bubble up via
    // GetDataOrPrintErrorAsync so the CLI surfaces server errors on stderr
    // with a non-zero exit — consistent with the rest of `mo`.
    private static async Task<int> PrintWorkflowRunYamlAsync(MohistCliApi api, string runId)
    {
        var (exitCode, data) = await api.GetDataOrPrintErrorAsync(WorkflowRunPath(runId, "/yaml"));
        if (exitCode != 0)
            return exitCode;
        if (data is null)
            return 1;

        var yaml = data["yaml"] as JsonValue;
        if (yaml is not null && yaml.TryGetValue<string>(out var text) && !string.IsNullOrEmpty(text))
        {
            api.Output.WriteLine(text);
            return 0;
        }

        api.Error.WriteLine("Workflow definition not found");
        return 1;
    }
}
