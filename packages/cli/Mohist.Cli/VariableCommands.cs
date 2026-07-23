using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mohist.Cli;

// Shared `variable list | get | set | unset` command group registered under
// `project`, `issue`, and `run`. The four leaves share one key-value language:
//
//   * dotted `<key>` (matches the `${{ vars.* }}` template path);
//   * `--stage <stage>` to scope reads / writes to that scope's Stage Variables
//     (absent = workflow-wide);
//   * `set <key> <positional>` always stores the value as a JSON string;
//   * `set <key> --value-json <json>` preserves the parsed JSON type;
//   * `unset <key>` deletes the current scope's declaration so the key inherits
//     from the parent scope (no `null` is persisted).
//
// Only `run variable list/get` accepts `--effective`, a read-only merge of the
// Project → Issue → Run effective chain. Project and Issue never expose
// effective reads; `--effective` on a write (any scope) is a local usage error.
//
// Single dispatch site: `BuildVariableGroup(api, scope)` returns the four-verb
// `variable` sub-tree. Each scope plugs this into its command tree (D1).
internal static class VariableCommands
{
    private static readonly ResourceDescriptor VariableBundleDescriptor = new(
        ResourceCardinality.Single,
        ["vars", "stages"]);

    public static Command BuildVariableGroup(MohistCliApi api, VariableScopeKind scope)
    {
        var group = new Command(
            "variable",
            scope switch
            {
                VariableScopeKind.Project => "Read or write the project's Variables (list / get / set / unset).",
                VariableScopeKind.Issue => "Read or write the issue's Variables (list / get / set / unset).",
                VariableScopeKind.Run => "Read or write the run's Variables (list / get / set / unset). Run-only --effective exposes the Project → Issue → Run merge.",
                _ => "Variable commands.",
            });

        group.Subcommands.Add(BuildList(api, scope));
        group.Subcommands.Add(BuildGet(api, scope));
        group.Subcommands.Add(BuildSet(api, scope));
        group.Subcommands.Add(BuildUnset(api, scope));
        return group;
    }

    // ────────────────────────────────────────────────────────────────────
    //  list
    // ────────────────────────────────────────────────────────────────────

    private static Command BuildList(MohistCliApi api, VariableScopeKind scope)
    {
        var cmd = new Command(
            "list",
            "List the scope's own Variables. With --stage, returns the scope's raw stage slice. Run-only --effective returns the merged Project → Issue → Run values.");
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var stageOpt = StageOpt();
        var effectiveOpt = scope == VariableScopeKind.Run ? EffectiveOpt() : null;
        var jsonOpt = MohistCliCommands.JsonSelectionOption();

        var numberArg = scope == VariableScopeKind.Issue ? NumberArg() : null;
        var runIdArg = scope == VariableScopeKind.Run ? RunIdArg() : null;
        var issueOpt = scope == VariableScopeKind.Run ? IssueOption() : null;

        AddCommonArgs(cmd, scope, numberArg, runIdArg, issueOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(stageOpt);
        if (effectiveOpt is not null)
            cmd.Options.Add(effectiveOpt);
        cmd.Options.Add(jsonOpt);

        cmd.SetAction(ctx =>
        {
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var stage = ctx.GetValue(stageOpt);
            var stageProvided = ctx.GetResult(stageOpt) is not null;
            var effective = effectiveOpt is null ? false : ctx.GetValue(effectiveOpt);
            var jsonProvided = ctx.GetResult(jsonOpt) is not null;
            var json = ctx.GetValue(jsonOpt);
            var selection = JsonSelection.Parse(VariableBundleDescriptor, jsonProvided, json);
            var number = numberArg is null ? null : ctx.GetValue(numberArg);
            var runId = runIdArg is null ? null : ctx.GetValue(runIdArg);
            var issue = issueOpt is null ? null : ctx.GetValue(issueOpt);
            return ListAsync();

            async Task<int> ListAsync()
            {
                if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
                    return api.WriteJsonSelectionResult(VariableBundleDescriptor, selection);

                if (scope != VariableScopeKind.Run && effective)
                {
                    await WriteEffectiveRejectedAsync(api, scope, "list").ConfigureAwait(false);
                    return CliExitCode.For(CliExitOutcome.UsageFailure);
                }
                var address = await ResolveScopeAddressAsync(
                    api, scope, project, projectId, number, runId, issue).ConfigureAwait(false);
                if (address.Exit != 0)
                    return address.Exit;

                var path = scope == VariableScopeKind.Run && effective
                    ? StageQueryString(BuildEffectiveListPath(address.RunId!, stage), stage)
                    : BuildVariablesPath(scope, address);

                return await api.PrintResourceAsync(
                    path,
                    VariableBundleDescriptor,
                    selection,
                    data => api.RenderTableAsync(data, MohistCliApi.TableShape.WorkflowVariables)).ConfigureAwait(false);
            }
        });
        return cmd;
    }

    // ────────────────────────────────────────────────────────────────────
    //  get
    // ────────────────────────────────────────────────────────────────────

    private static Command BuildGet(MohistCliApi api, VariableScopeKind scope)
    {
        var cmd = new Command(
            "get",
            "Get one value at a dotted key path from the scope's own Variables. With --effective (run only), the value comes from the merged Project → Issue → Run chain.");
        var keyArg = KeyArg();
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var stageOpt = StageOpt();
        var effectiveOpt = scope == VariableScopeKind.Run ? EffectiveOpt() : null;

        var numberArg = scope == VariableScopeKind.Issue ? NumberArg() : null;
        var runIdArg = scope == VariableScopeKind.Run ? RunIdArg() : null;
        var issueOpt = scope == VariableScopeKind.Run ? IssueOption() : null;

        AddCommonArgs(cmd, scope, numberArg, runIdArg, issueOpt);
        cmd.Arguments.Add(keyArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(stageOpt);
        if (effectiveOpt is not null)
            cmd.Options.Add(effectiveOpt);

        cmd.SetAction(ctx =>
        {
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var stage = ctx.GetValue(stageOpt);
            var stageProvided = ctx.GetResult(stageOpt) is not null;
            var effective = effectiveOpt is null ? false : ctx.GetValue(effectiveOpt);
            var rawKey = ctx.GetValue(keyArg);
            var number = numberArg is null ? null : ctx.GetValue(numberArg);
            var runId = runIdArg is null ? null : ctx.GetValue(runIdArg);
            var issue = issueOpt is null ? null : ctx.GetValue(issueOpt);
            return GetAsync();

            async Task<int> GetAsync()
            {
                if (!VariableKeyPath.TryParse(rawKey, out var segments, out var keyError))
                {
                    await api.Error.WriteLineAsync(keyError!).ConfigureAwait(false);
                    return CliExitCode.For(CliExitOutcome.UsageFailure);
                }

                if (scope != VariableScopeKind.Run && effective)
                {
                    await WriteEffectiveRejectedAsync(api, scope, "get").ConfigureAwait(false);
                    return CliExitCode.For(CliExitOutcome.UsageFailure);
                }

                var address = await ResolveScopeAddressAsync(
                    api, scope, project, projectId, number, runId, issue).ConfigureAwait(false);
                if (address.Exit != 0)
                    return address.Exit;

                if (scope == VariableScopeKind.Run && effective)
                {
                    var effectivePath = $"/api/workflow-runs/{MohistCliCommands.Escape(address.RunId!)}/variables/effective/{MohistCliCommands.Escape(string.Join(".", segments))}";
                    return await api.PrintGetSafeAsync(StageQueryString(effectivePath, stage)).ConfigureAwait(false);
                }

                var bundlePath = BuildVariablesPath(scope, address);
                return await PrintBundleValueAtKeyAsync(api, bundlePath, segments, stage).ConfigureAwait(false);
            }
        });
        return cmd;
    }

    // ────────────────────────────────────────────────────────────────────
    //  set
    // ────────────────────────────────────────────────────────────────────

    private static Command BuildSet(MohistCliApi api, VariableScopeKind scope)
    {
        var cmd = new Command(
            "set",
            "Set one value at a dotted key path. Positional value stores a JSON string; --value-json <json> stores the parsed type. The two inputs are mutually exclusive and exactly one is required.");
        var keyArg = KeyArg();
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var stageOpt = StageOpt();
        var valueJsonOpt = ValueJsonOpt();
        var effectiveOpt = EffectiveOpt();

        var numberArg = scope == VariableScopeKind.Issue ? NumberArg() : null;
        var runIdArg = scope == VariableScopeKind.Run ? RunIdArg() : null;
        var issueOpt = scope == VariableScopeKind.Run ? IssueOption() : null;

        var positionalValue = new Argument<string?>("value")
        {
            Description = "Positional value (stored verbatim as a JSON string). Optional when --value-json is supplied.",
            Arity = ArgumentArity.ZeroOrOne,
            DefaultValueFactory = _ => null,
        };

        AddCommonArgs(cmd, scope, numberArg, runIdArg, issueOpt);
        cmd.Arguments.Add(keyArg);
        cmd.Arguments.Add(positionalValue);

        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(stageOpt);
        cmd.Options.Add(valueJsonOpt);
        cmd.Options.Add(effectiveOpt);

        // Mutual exclusion + exactly-one enforcement runs locally before the
        // action body so HTTP is never called on malformed input.
        cmd.Validators.Add(result =>
        {
            var hasPositional = result.GetResult(positionalValue) is not null;
            var hasJson = result.GetResult(valueJsonOpt) is not null;
            if (hasPositional && hasJson)
            {
                result.AddError("positional value and --value-json are mutually exclusive; provide exactly one.");
                return;
            }
            if (!hasPositional && !hasJson)
            {
                result.AddError("a positional value or --value-json is required.");
                return;
            }
        });

        cmd.SetAction(ctx =>
        {
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var stage = ctx.GetValue(stageOpt);
            var rawKey = ctx.GetValue(keyArg);
            var positionalString = ctx.GetValue(positionalValue);
            var valueJsonRaw = ctx.GetValue(valueJsonOpt);
            var effective = ctx.GetValue(effectiveOpt);
            var number = numberArg is null ? null : ctx.GetValue(numberArg);
            var runId = runIdArg is null ? null : ctx.GetValue(runIdArg);
            var issue = issueOpt is null ? null : ctx.GetValue(issueOpt);
            return SetAsync();

            async Task<int> SetAsync()
            {
                if (effective)
                {
                    await api.Error.WriteLineAsync(
                        "--effective is read-only and cannot be combined with 'set'.").ConfigureAwait(false);
                    return CliExitCode.For(CliExitOutcome.UsageFailure);
                }

                if (!VariableKeyPath.TryParse(rawKey, out var segments, out var keyError))
                {
                    await api.Error.WriteLineAsync(keyError!).ConfigureAwait(false);
                    return CliExitCode.For(CliExitOutcome.UsageFailure);
                }

                if (stage is not null && stage.Trim().Length == 0)
                {
                    await api.Error.WriteLineAsync("--stage must not be empty").ConfigureAwait(false);
                    return CliExitCode.For(CliExitOutcome.UsageFailure);
                }

                JsonNode leaf;
                if (positionalString is not null)
                {
                    leaf = JsonValue.Create(positionalString)!;
                }
                else
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(valueJsonRaw!);
                        leaf = JsonNode.Parse(doc.RootElement.GetRawText())!;
                    }
                    catch (JsonException ex)
                    {
                        await api.Error.WriteLineAsync(
                            $"--value-json: invalid JSON ({ex.Message})").ConfigureAwait(false);
                        return CliExitCode.For(CliExitOutcome.UsageFailure);
                    }
                }

                var address = await ResolveScopeAddressAsync(
                    api, scope, project, projectId, number, runId, issue).ConfigureAwait(false);
                if (address.Exit != 0)
                    return address.Exit;

                var patchBody = VariableKeyPath.BuildSetPatch(segments, stage, leaf);
                var path = BuildVariablesPath(scope, address);
                return await api.PrintPatchAsync(path, patchBody).ConfigureAwait(false);
            }
        });
        return cmd;
    }

    // ────────────────────────────────────────────────────────────────────
    //  unset
    // ────────────────────────────────────────────────────────────────────

    private static Command BuildUnset(MohistCliApi api, VariableScopeKind scope)
    {
        var cmd = new Command(
            "unset",
            "Delete the current scope's declaration at a dotted key. The persisted document never stores a null; the key re-inherits from the parent scope.");
        var keyArg = KeyArg();
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var stageOpt = StageOpt();
        var effectiveOpt = EffectiveOpt();

        var numberArg = scope == VariableScopeKind.Issue ? NumberArg() : null;
        var runIdArg = scope == VariableScopeKind.Run ? RunIdArg() : null;
        var issueOpt = scope == VariableScopeKind.Run ? IssueOption() : null;

        AddCommonArgs(cmd, scope, numberArg, runIdArg, issueOpt);
        cmd.Arguments.Add(keyArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(stageOpt);
        cmd.Options.Add(effectiveOpt);

        cmd.SetAction(ctx =>
        {
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var stage = ctx.GetValue(stageOpt);
            var rawKey = ctx.GetValue(keyArg);
            var effective = ctx.GetValue(effectiveOpt);
            var number = numberArg is null ? null : ctx.GetValue(numberArg);
            var runId = runIdArg is null ? null : ctx.GetValue(runIdArg);
            var issue = issueOpt is null ? null : ctx.GetValue(issueOpt);
            return UnsetAsync();

            async Task<int> UnsetAsync()
            {
                if (effective)
                {
                    await api.Error.WriteLineAsync(
                        "--effective is read-only and cannot be combined with 'unset'.").ConfigureAwait(false);
                    return CliExitCode.For(CliExitOutcome.UsageFailure);
                }

                if (!VariableKeyPath.TryParse(rawKey, out var segments, out var keyError))
                {
                    await api.Error.WriteLineAsync(keyError!).ConfigureAwait(false);
                    return CliExitCode.For(CliExitOutcome.UsageFailure);
                }

                var address = await ResolveScopeAddressAsync(
                    api, scope, project, projectId, number, runId, issue).ConfigureAwait(false);
                if (address.Exit != 0)
                    return address.Exit;

                var patchBody = VariableKeyPath.BuildUnsetPatch(segments, stage);
                var path = BuildVariablesPath(scope, address);
                return await api.PrintPatchAsync(path, patchBody).ConfigureAwait(false);
            }
        });
        return cmd;
    }

    // ────────────────────────────────────────────────────────────────────
    //  Bundled-value extraction for scope-local `get`
    // ────────────────────────────────────────────────────────────────────

    // Fetch the scope's Variables bundle and print the value at the dotted key
    // path. The CLI does the traversal (matching the server's
    // `VariableBundle.GetByKeyPath` semantics) so a non-existent path prints
    // an `absent` indicator rather than the raw envelope.
    private static async Task<int> PrintBundleValueAtKeyAsync(
        MohistCliApi api,
        string bundlePath,
        IReadOnlyList<string> segments,
        string? stage)
    {
        var response = await api.ResponseReader.ReadAsync(
            HttpMethod.Get, bundlePath, cancellationToken: api.Invocation.CancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccess)
            return await new CliResultWriter(api.Invocation)
                .WriteFailureAsync(response.Failure!).ConfigureAwait(false);

        var bundle = response.Data as JsonObject;
        var root = ExtractScopeLocalRoot(bundle, stage);
        var value = TraverseKey(root, segments);

        if (value is null)
        {
            await api.Output.WriteLineAsync("(absent)").ConfigureAwait(false);
            return CliExitCode.For(CliExitOutcome.Success);
        }

        await api.Output.WriteLineAsync(value.ToJsonString(MohistCliApi.JsonOutputOptions))
            .ConfigureAwait(false);
        return CliExitCode.For(CliExitOutcome.Success);
    }

    private static JsonNode? ExtractScopeLocalRoot(JsonObject? bundle, string? stage)
    {
        if (bundle is null) return null;
        if (string.IsNullOrEmpty(stage))
            return bundle["vars"];
        var stages = bundle["stages"] as JsonObject;
        var stageEntry = stages?[stage!] as JsonObject;
        return stageEntry?["vars"];
    }

    // Mirrors the server's `VariableBundle.GetByKeyPath` traversal: descend
    // into nested objects one segment at a time; any non-object step returns
    // `null` (absent). The write side already rejected malformed keys; the
    // read side reports absentees with a sentinel.
    private static JsonNode? TraverseKey(JsonNode? root, IReadOnlyList<string> segments)
    {
        if (root is null) return null;
        JsonNode? current = root;
        foreach (var segment in segments)
        {
            if (current is not JsonObject obj)
                return null;
            current = obj[segment];
            if (current is null)
                return null;
        }
        return current;
    }

    // ────────────────────────────────────────────────────────────────────
    //  Address resolution
    // ────────────────────────────────────────────────────────────────────

    private readonly record struct ResolvedAddress(int Exit, string? ProjectId, string? IssueNumber, string? RunId)
    {
        public static ResolvedAddress Failure(int exit) => new(exit, null, null, null);
    }

    // Resolves the addressed scope's identifying ids and emits local usage
    // errors for malformed invocations (run-id-or-issue on non-run scopes,
    // both/neither target on the run scope). For Issue, the issue number is
    // required from the argument; for Run, the resolver in `RunCommands`
    // already enforces the dual-target rule.
    private static async Task<ResolvedAddress> ResolveScopeAddressAsync(
        MohistCliApi api,
        VariableScopeKind scope,
        string? project,
        string? projectId,
        string? number,
        string? runId,
        string? issue)
    {
        switch (scope)
        {
            case VariableScopeKind.Project:
                if (runId is not null || issue is not null || number is not null)
                {
                    await api.Error.WriteLineAsync(
                        "'run id', --issue, and <number> are only valid on 'issue variable' or 'run variable'.").ConfigureAwait(false);
                    return ResolvedAddress.Failure(CliExitCode.For(CliExitOutcome.UsageFailure));
                }
                var (p, pExit) = await api.ResolveProject(MergeProject(project, projectId)).ConfigureAwait(false);
                return pExit != 0
                    ? ResolvedAddress.Failure(pExit)
                    : new ResolvedAddress(0, p, null, null);
            case VariableScopeKind.Issue:
                if (runId is not null || issue is not null)
                {
                    await api.Error.WriteLineAsync(
                        "'run id' and --issue are only valid on 'run variable'.").ConfigureAwait(false);
                    return ResolvedAddress.Failure(CliExitCode.For(CliExitOutcome.UsageFailure));
                }
                if (string.IsNullOrWhiteSpace(number))
                {
                    await api.Error.WriteLineAsync(
                        "<number> is required for 'issue variable'.").ConfigureAwait(false);
                    return ResolvedAddress.Failure(CliExitCode.For(CliExitOutcome.UsageFailure));
                }
                var (ip, ipExit) = await api.ResolveProject(MergeProject(project, projectId)).ConfigureAwait(false);
                return ipExit != 0
                    ? ResolvedAddress.Failure(ipExit)
                    : new ResolvedAddress(0, ip, number, null);
            case VariableScopeKind.Run:
                var (resolvedRunId, resolveExit) = await RunCommands.ResolveRunTargetAsync(
                    api, runId, issue, MergeProject(project, projectId)).ConfigureAwait(false);
                return resolveExit != 0
                    ? ResolvedAddress.Failure(resolveExit)
                    : new ResolvedAddress(0, null, null, resolvedRunId);
            default:
                return ResolvedAddress.Failure(1);
        }
    }

    private static string BuildVariablesPath(VariableScopeKind scope, ResolvedAddress address)
    {
        return scope switch
        {
            VariableScopeKind.Project => $"/api/projects/{MohistCliCommands.Escape(address.ProjectId!)}/variables",
            VariableScopeKind.Issue => $"/api/projects/{MohistCliCommands.Escape(address.ProjectId!)}/issues/{MohistCliCommands.Escape(address.IssueNumber!)}/variables",
            VariableScopeKind.Run => $"/api/workflow-runs/{MohistCliCommands.Escape(address.RunId!)}/variables",
            _ => throw new InvalidOperationException("Unknown variable scope"),
        };
    }

    private static string BuildEffectiveListPath(string runId, string? stage) =>
        $"/api/workflow-runs/{MohistCliCommands.Escape(runId)}/variables/effective";

    // Appends a `?stage=<stage>` query parameter when the caller asked for one.
    private static string StageQueryString(string path, string? stage) =>
        string.IsNullOrEmpty(stage) ? path : $"{path}?stage={MohistCliCommands.Escape(stage)}";

    private static async Task WriteEffectiveRejectedAsync(MohistCliApi api, VariableScopeKind scope, string verb)
    {
        var scopeName = scope == VariableScopeKind.Project ? "project variable" : "issue variable";
        await api.Error.WriteLineAsync(
            $"--effective is only available for 'run variable list/get'; {scopeName} {verb} remains scope-local.").ConfigureAwait(false);
    }

    // ────────────────────────────────────────────────────────────────────
    //  Argument / option wiring
    // ────────────────────────────────────────────────────────────────────

    private static Argument<string?> KeyArg() => new("key")
    {
        Description = "Dotted key path (e.g. agent.model). Matches the ${{ vars.* }} template path.",
        Arity = ArgumentArity.ExactlyOne,
    };

    private static Option<string?> StageOpt() => new("--stage")
    {
        Description = "Read or write that scope's Stage Variables for the named stage (workflow-wide when omitted).",
    };

    private static Option<string?> ValueJsonOpt() => new("--value-json")
    {
        Description = "Typed value as inline JSON. Mutually exclusive with the positional value; parsed with JsonDocument locally and rejected (exit 2) on parse failure.",
    };

    private static Option<bool> EffectiveOpt() => new("--effective")
    {
        Description = "Run-only: read the merged Project → Issue → Run values (read-only; rejects writes and is unavailable outside the run scope).",
    };

    private static Argument<string?> NumberArg() => new("number")
    {
        Description = "Issue number",
    };

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

    private static void AddCommonArgs(Command cmd, VariableScopeKind scope, Argument<string?>? numberArg, Argument<string?>? runIdArg, Option<string?>? issueOpt)
    {
        if (scope == VariableScopeKind.Issue && numberArg is not null)
            cmd.Arguments.Add(numberArg);
        if (scope == VariableScopeKind.Run)
        {
            if (runIdArg is not null)
                cmd.Arguments.Add(runIdArg);
            if (issueOpt is not null)
                cmd.Options.Add(issueOpt);
        }
    }

    private static string? MergeProject(string? project, string? projectId) =>
        !string.IsNullOrWhiteSpace(project) ? project : projectId;
}

internal enum VariableScopeKind
{
    Project,
    Issue,
    Run,
}
