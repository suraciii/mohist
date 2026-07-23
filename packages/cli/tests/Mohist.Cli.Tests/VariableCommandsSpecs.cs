using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

[CollectionDefinition("VariableCommands", DisableParallelization = true)]
public sealed class VariableCommandsCollectionDefinition
{
}

// T-002 (issue-478): unified `variable list/get/set/unset` shared across
// `project`, `issue`, `run`. The three scopes share one key-value language:
// dotted `<key>` matching the `${{ vars.* }}` template path, `--stage <stage>`
// selecting Stage Variables, positional value stored as a JSON string, and
// `--value-json <json>` preserving the parsed type. The two set inputs are
// mutually exclusive and exactly one is required for `set`.
//
// Tests pin:
//   * the four verbs exist on each scope;
//   * the same local validation rules apply to all three scopes (no HTTP on
//     malformed input);
//   * scope-local `list/get` never merge across scopes;
//   * `--effective` is Run-only and refuses writes;
//   * Run target resolution reuses the contract shared by every other `run`
//     verb (positional ID or `--issue`, exactly one).
//
// The server-side route rename (`workflow-profile/variables` -> `variables`)
// and the new `VariableBundleShapeValidator` live in T-001. These tests
// already target the clean paths so they assert the post-T-001 contract.
[Collection("VariableCommands")]
public class VariableCommandsSpecs
{
    private const string WrId = "wr_variable01";
    private const string WrFromIssue = "wr_from_issue42";
    private const int IssueNumber = 42;

    // ────────────────────────────────────────────────────────────────────
    //  Command-tree shape (all three scopes)
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ProjectVariable_Help_ListsFourVerbs()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();
        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "variable", "--help"], output, error, fs, executor);
        Assert.Equal(0, exitCode);
        AssertGroupListsFourVerbs(output.ToString());
    }

    [Fact]
    public async Task IssueVariable_Help_ListsFourVerbs()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();
        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "variable", "--help"], output, error, fs, executor);
        Assert.Equal(0, exitCode);
        AssertGroupListsFourVerbs(output.ToString());
    }

    [Fact]
    public async Task RunVariable_Help_ListsFourVerbs()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();
        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "variable", "--help"], output, error, fs, executor);
        Assert.Equal(0, exitCode);
        AssertGroupListsFourVerbs(output.ToString());
        // The Run variant advertises --effective; project / issue must not.
        Assert.Contains("--effective", output.ToString());
    }

    [Fact]
    public async Task ProjectVariable_ListHelp_ShowsStageAndJson()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();
        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "variable", "list", "--help"], output, error, fs, executor);
        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("--stage", stdout);
        Assert.Contains("--json", stdout);
        // `--effective` is allowed to appear in the description (it is part
        // of the shared feature story); it must NOT be advertised as an
        // option on the project / issue scope.
        var optionsSection = ExtractOptionsSection(stdout);
        Assert.DoesNotContain("--effective", optionsSection);
    }

    private static string ExtractOptionsSection(string stdout)
    {
        // System.CommandLine's help writes `Options:\n  --foo  ...\nCommands:\n ...`.
        // The OPTIONS section is everything between the `Options:` heading and
        // the next section heading (`Arguments:` or `Commands:`).
        var optionsStart = stdout.IndexOf("Options:", StringComparison.Ordinal);
        if (optionsStart < 0) return string.Empty;
        var afterOptions = optionsStart + "Options:".Length;
        var argsStart = stdout.IndexOf("Arguments:", afterOptions, StringComparison.Ordinal);
        var commandsStart = stdout.IndexOf("Commands:", afterOptions, StringComparison.Ordinal);
        var endCandidates = new[] { argsStart, commandsStart }.Where(i => i > 0).ToArray();
        var end = endCandidates.Length == 0 ? stdout.Length : endCandidates.Min();
        return stdout.Substring(afterOptions, end - afterOptions);
    }

    private static void AssertGroupListsFourVerbs(string stdout)
    {
        Assert.Contains("list", stdout);
        Assert.Contains("get", stdout);
        Assert.Contains("set", stdout);
        Assert.Contains("unset", stdout);
    }

    // ────────────────────────────────────────────────────────────────────
    //  set — positional value stores as JSON string
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task IssueSet_PositionalValue_StoresJsonString()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Patch
                && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42/variables")
            {
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "variable", "set", "42", "agent.model", "openai/gpt-5"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var patch = handler.Requests.Single(r => r.Method == HttpMethod.Patch);
        var body = JsonNode.Parse(patch.Body!) as JsonObject;
        var vars = body!["vars"] as JsonObject;
        var agent = vars!["agent"] as JsonObject;
        Assert.Equal("openai/gpt-5", agent!["model"]?.GetValue<string>());
        // Positional value is always a JSON string, not a different type.
        Assert.Equal(JsonValueKind.String, ((JsonValue)agent["model"]!).GetValue<JsonElement>().ValueKind);
    }

    [Fact]
    public async Task ProjectSet_PositionalNumericLooking_KeepsStringType()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Patch
                && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/variables")
            {
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "variable", "set", "change.prNumber", "42"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var patch = handler.Requests.Single(r => r.Method == HttpMethod.Patch);
        var body = JsonNode.Parse(patch.Body!) as JsonObject;
        var change = body!["vars"]!["change"] as JsonObject;
        var prNumber = change!["prNumber"];
        Assert.Equal("42", prNumber?.GetValue<string>());
        Assert.Equal(JsonValueKind.String, ((JsonValue)prNumber!).GetValue<JsonElement>().ValueKind);
    }

    // ────────────────────────────────────────────────────────────────────
    //  set --stage + --value-json: Stage and type preserved
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task IssueSet_WithStageAndValueJson_StoresTypedStageValue()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Patch
                && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42/variables")
            {
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["issue", "variable", "set", "42", "review.strict",
                "--value-json", "true",
                "--stage", "check"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var patch = handler.Requests.Single(r => r.Method == HttpMethod.Patch);
        var body = JsonNode.Parse(patch.Body!) as JsonObject;
        var stages = body!["stages"] as JsonObject;
        var check = stages!["check"] as JsonObject;
        var vars = check!["vars"] as JsonObject;
        Assert.NotNull(vars);
        var review = vars["review"] as JsonObject;
        Assert.NotNull(review);
        var strict = review!["strict"];
        Assert.NotNull(strict);
        Assert.True(strict!.GetValue<bool>());
        // The body must NOT carry a workflow-wide `vars` entry — `--stage`
        // redirects everything to `stages.<stage>.vars`.
        Assert.False(body!.ContainsKey("vars"));
    }

    [Fact]
    public async Task RunSet_WorkflowWide_PatchesRunVariablesVars()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Patch
                && req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrId}/variables")
            {
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["run", "variable", "set", WrId, "agent.model", "openai/gpt-5"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var patch = handler.Requests.Single(r => r.Method == HttpMethod.Patch);
        var body = JsonNode.Parse(patch.Body!) as JsonObject;
        var agent = body!["vars"]!["agent"] as JsonObject;
        Assert.Equal("openai/gpt-5", agent!["model"]?.GetValue<string>());
    }

    // ────────────────────────────────────────────────────────────────────
    //  set — local usage failures (exit 2, no HTTP)
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task IssueSet_BothPositionalAndValueJson_FailsLocallyExitTwoNoHttp()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["issue", "variable", "set", "42", "agent.model", "openai/gpt-5",
                "--value-json", "true"],
            output, error, fs, executor);

        Assert.Equal(2, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("mutually exclusive", error.ToString());
    }

    [Fact]
    public async Task RunSet_NeitherValue_FailsLocallyExitTwoNoHttp()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["run", "variable", "set", WrId, "agent.model"],
            output, error, fs, executor);

        Assert.Equal(2, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("required", error.ToString());
    }

    [Fact]
    public async Task ProjectSet_InvalidValueJson_FailsLocallyExitTwoNoHttp()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["project", "variable", "set", "review.strict", "--value-json", "{not json"],
            output, error, fs, executor);

        Assert.Equal(2, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("invalid JSON", error.ToString());
    }

    [Fact]
    public async Task IssueSet_EmptyKey_FailsLocallyExitTwoNoHttp()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "variable", "set", "42", "  ", "value"],
            output, error, fs, executor);

        Assert.Equal(2, exitCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RunSet_EmptySegmentKey_FailsLocallyExitTwoNoHttp()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "variable", "set", WrId, "a..b", "value"],
            output, error, fs, executor);

        Assert.Equal(2, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("empty", error.ToString());
    }

    // ────────────────────────────────────────────────────────────────────
    //  unset
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task IssueUnset_WorkflowWide_EmitsJsonNullLeaf()
    {
        var stored = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["foo"] = "bar",
            ["other"] = "kept",
        };
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(async (req, _) =>
        {
            if (req.Method == HttpMethod.Patch
                && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42/variables")
            {
                var bodyText = req.Content is null ? null : await req.Content.ReadAsStringAsync();
                var body = JsonNode.Parse(bodyText ?? "{}") as JsonObject;
                var overlay = body?["vars"] as JsonObject;
                if (overlay is not null)
                {
                    foreach (var kvp in overlay)
                    {
                        if (kvp.Value is null || kvp.Value.GetValueKind() == JsonValueKind.Null)
                            stored.Remove(kvp.Key);
                        else
                            stored[kvp.Key] = kvp.Value.GetValueKind() == JsonValueKind.String
                                ? kvp.Value.GetValue<string>()
                                : kvp.Value.ToJsonString();
                    }
                }
                return RecordingHttpHandler.Json(new { success = true, data = new { vars = stored } });
            }
            return RecordingHttpHandler.Json(new { success = true, data = new { } });
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "variable", "unset", "42", "agent.model"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var patch = handler.Requests.Single(r => r.Method == HttpMethod.Patch);
        var body = JsonNode.Parse(patch.Body!) as JsonObject;
        var vars = body!["vars"] as JsonObject;
        Assert.NotNull(vars!["agent"]);
        Assert.Null(vars["agent"]!["model"]);
    }

    [Fact]
    public async Task RunUnset_WithStage_ClearsStageDeclaration()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Patch
                && req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrId}/variables")
            {
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "variable", "unset", WrId, "agent.variant", "--stage", "check"],
            output, error, fs, executor);

        Assert.True(exitCode == 0, $"exit={exitCode} stdout={output} err={error}");
        var patch = handler.Requests.Single(r => r.Method == HttpMethod.Patch);
        var body = JsonNode.Parse(patch.Body!) as JsonObject;
        var stages = body!["stages"] as JsonObject;
        var check = stages!["check"] as JsonObject;
        var vars = check!["vars"] as JsonObject;
        Assert.NotNull(vars);
        Assert.Null(vars["agent"]!["variant"]);
        // No workflow-wide `vars` entry when --stage is present.
        Assert.False(body!.ContainsKey("vars"));
    }

    // ────────────────────────────────────────────────────────────────────
    //  Scope-local reads
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ProjectList_HitsProjectVariablesPath()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get
                && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/variables")
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new { vars = new { } },
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "variable", "list"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Contains(handler.Requests, r =>
            r.Method == HttpMethod.Get && r.RequestUri?.PathAndQuery == "/api/projects/proj_abc/variables");
    }

    [Fact]
    public async Task IssueGet_HitsIssueVariablesPath()
    {
        var stored = new JsonObject
        {
            ["vars"] = new JsonObject
            {
                ["agent"] = new JsonObject { ["model"] = "openai/gpt-5" },
            },
        };
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get
                && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42/variables")
            {
                return RecordingHttpHandler.Json(new { success = true, data = stored });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "variable", "get", "42", "agent.model"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        // Only the scope-local GET was issued.
        Assert.Single(handler.Requests);
        Assert.Equal("/api/projects/proj_abc/issues/42/variables",
            handler.Requests[0].RequestUri?.PathAndQuery);
    }

    // ────────────────────────────────────────────────────────────────────
    //  --effective rejects outside the run scope
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ProjectList_Effective_FailsLocallyExitTwoNoHttp()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "variable", "list", "--effective"],
            output, error, fs, executor);

        Assert.Equal(2, exitCode);
        Assert.Empty(handler.Requests);
        // `--effective` is not a recognized option on `project variable list`,
        // so System.CommandLine surfaces the unrecognized-option error. Either
        // message is acceptable — both are local-only, exit 2, no HTTP.
        var stderr = error.ToString();
        Assert.True(
            stderr.Contains("only available for", StringComparison.Ordinal)
            || stderr.Contains("Unrecognized", StringComparison.Ordinal),
            $"unexpected stderr: {stderr}");
    }

    [Fact]
    public async Task IssueGet_Effective_FailsLocallyExitTwoNoHttp()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "variable", "get", "42", "agent.model", "--effective"],
            output, error, fs, executor);

        Assert.Equal(2, exitCode);
        Assert.Empty(handler.Requests);
    }

    // ────────────────────────────────────────────────────────────────────
    //  Run --effective
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunList_Effective_HitsEffectiveEndpoint()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get
                && req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrId}/variables/effective")
            {
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "variable", "list", WrId, "--effective"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Contains(handler.Requests, r =>
            r.Method == HttpMethod.Get
            && r.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrId}/variables/effective");
    }

    [Fact]
    public async Task RunGet_EffectiveWithStage_HitsStageEffectiveEndpoint()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get
                && req.RequestUri?.AbsolutePath == $"/api/workflow-runs/{WrId}/variables/effective/agent.variant")
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = "xhigh",
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["run", "variable", "get", WrId, "agent.variant", "--effective", "--stage", "check"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Contains(handler.Requests, r =>
            r.Method == HttpMethod.Get
            && r.RequestUri?.AbsolutePath == $"/api/workflow-runs/{WrId}/variables/effective/agent.variant");
    }

    [Fact]
    public async Task RunSet_Effective_FailsLocallyExitTwoNoHttp()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["run", "variable", "set", WrId, "agent.model", "openai/gpt-5", "--effective"],
            output, error, fs, executor);

        Assert.Equal(2, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("read-only", error.ToString());
    }

    [Fact]
    public async Task RunUnset_Effective_FailsLocallyExitTwoNoHttp()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["run", "variable", "unset", WrId, "agent.model", "--effective"],
            output, error, fs, executor);

        Assert.Equal(2, exitCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RunList_EffectiveWithStage_HitsStageEffectiveEndpoint()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get
                && req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrId}/variables/effective?stage=check")
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new { vars = new { strict = true }, stages = new { } },
                });
            }

            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["run", "variable", "list", WrId, "--effective", "--stage", "check"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Contains(handler.Requests, r =>
            r.Method == HttpMethod.Get
            && r.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrId}/variables/effective?stage=check");
    }

    // ────────────────────────────────────────────────────────────────────
    //  Run target resolution
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunList_BothRunIdAndIssue_FailsLocallyExitTwoNoHttp()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["run", "variable", "list", WrId, "--issue", IssueNumber.ToString()],
            output, error, fs, executor);

        Assert.Equal(2, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("not both", error.ToString());
    }

    [Fact]
    public async Task RunList_NeitherTarget_FailsLocallyExitTwoNoHttp()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "variable", "list"],
            output, error, fs, executor);

        Assert.Equal(2, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("required", error.ToString());
    }

    [Fact]
    public async Task RunList_WithIssue_ResolvesBoundRunAndReadsVariables()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get
                && req.RequestUri?.PathAndQuery == $"/api/projects/proj_abc/issues/{IssueNumber}")
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new
                    {
                        number = IssueNumber,
                        workflowRunId = WrFromIssue,
                    },
                });
            }
            if (req.Method == HttpMethod.Get
                && req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrFromIssue}/variables")
            {
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["run", "variable", "list", "--issue", IssueNumber.ToString()],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        // Issue GET happened, then the resolved Run's variables GET.
        Assert.Contains(handler.Requests, r =>
            r.Method == HttpMethod.Get
            && r.RequestUri?.PathAndQuery == $"/api/projects/proj_abc/issues/{IssueNumber}");
        Assert.Contains(handler.Requests, r =>
            r.Method == HttpMethod.Get
            && r.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrFromIssue}/variables");
    }

    // ────────────────────────────────────────────────────────────────────
    //  --json output selection (output-only contract)
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunList_JsonStages_PrintsOnlyStagesField()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get
                && req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrId}/variables")
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new
                    {
                        vars = new { foo = "bar" },
                        stages = new
                        {
                            check = new { vars = new { strict = true } },
                        },
                    },
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "variable", "list", WrId, "--json", "stages"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.True(!string.IsNullOrEmpty(stdout), $"stdout empty. stderr={error}");
        Assert.True(stdout.Contains("\"stages\""), $"no 'stages' in stdout: {stdout}");
        // The top-level `vars` field is dropped by the `--json stages`
        // selector at parse time; the response's nested `stages.check.vars`
        // is a different field and stays. Assert no top-level `"vars":`
        // appears before the first `"stages"` key.
        var firstStageAt = stdout.IndexOf("\"stages\"", StringComparison.Ordinal);
        Assert.True(firstStageAt >= 0, "stages key not found");
        var leadingSlice = stdout.Substring(0, firstStageAt);
        Assert.False(leadingSlice.Contains("\"vars\":", StringComparison.Ordinal),
            $"top-level vars should be dropped, but stdout had it: {stdout}");
    }

    [Fact]
    public async Task RunList_JsonDiscovery_ListsAvailableFields()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get
                && req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrId}/variables")
            {
                return RecordingHttpHandler.Json(new { success = true, data = new { vars = new { }, stages = new { } } });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "variable", "list", WrId, "--json"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        // Discovery shows the descriptor's accepted fields — output only.
        Assert.Contains("vars", output.ToString());
        Assert.Contains("stages", output.ToString());
    }

    [Fact]
    public async Task RunSet_JsonOptionIsNeverAcceptedAsValueInput()
    {
        // `--json true` selects the output selector; it does NOT feed the
        // value leaf, and `set <key> --json` still requires a value source.
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["run", "variable", "set", WrId, "agent.model", "--json", "true"],
            output, error, fs, executor);

        // No positional value and no --value-json — exit 2 locally.
        Assert.Equal(2, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("required", error.ToString());
    }
}

// ────────────────────────────────────────────────────────────────────
//  VariableKeyPath helper
// ────────────────────────────────────────────────────────────────────

public class VariableKeyPathTests
{
    [Fact]
    public void TryParse_AcceptsSingleSegment()
    {
        Assert.True(VariableKeyPath.TryParse("foo", out var segments, out var error));
        Assert.Null(error);
        Assert.Equal(["foo"], segments);
    }

    [Fact]
    public void TryParse_AcceptsDottedSegments()
    {
        Assert.True(VariableKeyPath.TryParse("agent.model", out var segments, out _));
        Assert.Equal(["agent", "model"], segments);
    }

    [Fact]
    public void TryParse_TrimsWhitespace()
    {
        Assert.True(VariableKeyPath.TryParse(" agent . model ", out var segments, out _));
        Assert.Equal(["agent", "model"], segments);
    }

    [Fact]
    public void TryParse_RejectsNullOrWhitespace()
    {
        Assert.False(VariableKeyPath.TryParse(null, out _, out var error));
        Assert.False(VariableKeyPath.TryParse("   ", out _, out _));
        Assert.False(VariableKeyPath.TryParse("", out _, out _));
        Assert.NotNull(error);
    }

    [Fact]
    public void TryParse_RejectsEmptySegment()
    {
        Assert.False(VariableKeyPath.TryParse("a..b", out _, out var error));
        Assert.False(VariableKeyPath.TryParse("a.", out _, out _));
        Assert.False(VariableKeyPath.TryParse(".a", out _, out _));
        Assert.NotNull(error);
    }

    [Fact]
    public void BuildSetPatch_WorkflowWide_NestsUnderVars()
    {
        var body = VariableKeyPath.BuildSetPatch(["agent", "model"], null, JsonValue.Create("openai/gpt-5")!);
        var env = body["vars"]!["agent"]!["model"];
        Assert.Equal("openai/gpt-5", env?.GetValue<string>());
        Assert.False(body.ContainsKey("stages"));
    }

    [Fact]
    public void BuildSetPatch_WithStage_NestsUnderStages()
    {
        var body = VariableKeyPath.BuildSetPatch(["agent", "variant"], "check", JsonValue.Create(true)!);
        var env = body["stages"]!["check"]!["vars"]!["agent"]!["variant"];
        Assert.True(env?.GetValue<bool>());
        Assert.False(body.ContainsKey("vars"));
    }

    [Fact]
    public void BuildUnsetPatch_EmitsJsonNullLeaf()
    {
        var body = VariableKeyPath.BuildUnsetPatch(["agent", "model"], null);
        // The JSON null leaf is encoded by setting the JsonObject indexer to
        // `null` — the property exists in the document but holds a null value.
        // We assert the rendered JSON encodes the literal `null`.
        var rendered = body.ToJsonString();
        Assert.Contains("\"model\":null", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildUnsetPatch_WithStage_NestsUnderStageVars()
    {
        var body = VariableKeyPath.BuildUnsetPatch(["agent", "variant"], "check");
        // Same JSON-null-leaf encoding, nested under `stages.check.vars`.
        var rendered = body.ToJsonString();
        Assert.Contains("\"variant\":null", rendered, StringComparison.Ordinal);
        Assert.Contains("\"check\":{", rendered, StringComparison.Ordinal);
    }
}
