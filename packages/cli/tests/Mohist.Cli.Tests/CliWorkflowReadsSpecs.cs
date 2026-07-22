using System.Net;
using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

// Read commands under `mo workflow` — get (with `show` as a transitional
// alias) / variables / events / list-sessions. These address a WorkflowRun
// directly by id (no project or issue number required) and follow the strict
// output-format / subresource / associated-resource distinction recorded in
// design/cli.md and the workflow-run-reads spec. Spec scope is intentionally
// narrow: HTTP interaction shape (path, query, body), server-error
// surfacing, and the shape→renderer mapping. Rendering details are covered by
// the table renderer specs in the project workflow surface; here we assert
// that the CLI picks the right shape for each command and honors the standard
// `-o` plumbing.
//
// The `status` command is gone (its compact view folded into `get`'s default
// table output, per workflow-run-reads/spec.md#the-redundant-status-command-is-removed)
// and the `show` command is now a transitional alias of `get`
// (workflow-run-reads/spec.md#show-is-a-transitional-alias-of-get). Alias
// parity is asserted by re-running the same scenario under both names and
// diffing the recorded HTTP requests + stdout + exit code.
public class CliWorkflowReadsSpecs
{
    private const string WrId = "wr_read01";

    private static object SampleStatus(string id = WrId, string status = "pending", string? currentStage = "build") => new
    {
        workflowRunId = id,
        status,
        currentStage,
        assignedTo = (string?)null,
        stages = new object[]
        {
            new
            {
                stage = "plan",
                status = "completed",
                order = 0,
                tasks = Array.Empty<object>(),
                checks = Array.Empty<object>(),
                approvalStatus = (object?)null,
                failure = (object?)null,
            },
            new
            {
                stage = "build",
                status = "running",
                order = 1,
                tasks = new object[]
                {
                    new { id = "t1", title = "Build it", uses = "agent/default", status = "running" },
                },
                checks = Array.Empty<object>(),
                approvalStatus = (object?)null,
                failure = (object?)null,
            },
        },
        pendingWork = (object?)null,
        failure = (object?)null,
        availableActions = Array.Empty<object>(),
        metadata = new { name = "Mohist Local Workflow", labels = new Dictionary<string, string>(), createdAt = "2026-07-05T00:00:00Z" },
    };

    private static object SampleDetail(string id = WrId) => new
    {
        status = SampleStatus(id),
        issueRef = new { number = 42, title = "Close the agent subscriptions gap" },
    };

    // A single canned responder returns the same payload for /{runId} and
    // /{runId}/yaml. Both `get` and its `show` alias share the handler, so we
    // intentionally capture the requests per scenario.
    private static (RecordingHttpHandler Handler, HttpClient Http, StringWriter Output, StringWriter Error, FakeFileSystem Fs, FakeCommandExecutor Executor)
        CreateWorkflowReadSetup() => CliTestFactory.CreateSync();

    private static void RunUnderBothNames(
        RecordingHttpHandler handler,
        HttpClient http,
        StringWriter output,
        StringWriter error,
        FakeFileSystem fs,
        FakeCommandExecutor executor,
        string[] runIdArgs,
        out int exit,
        out string stdoutText,
        out string stderrText)
    {
        output.GetStringBuilder().Clear();
        error.GetStringBuilder().Clear();
        var canonicalExit = MohistCliCommands.RunAsync(
            http, new[] { "workflow", "get" }.Concat(runIdArgs).ToArray(), output, error, fs, executor).GetAwaiter().GetResult();
        var canonicalStdout = output.ToString();
        var canonicalStderr = error.ToString();
        var canonicalRequests = handler.Requests.ToList();

        output.GetStringBuilder().Clear();
        error.GetStringBuilder().Clear();
        var aliasExit = MohistCliCommands.RunAsync(
            http, new[] { "workflow", "show" }.Concat(runIdArgs).ToArray(), output, error, fs, executor).GetAwaiter().GetResult();
        var aliasStdout = output.ToString();
        var aliasStderr = error.ToString();
        var aliasRequests = handler.Requests.Skip(canonicalRequests.Count).ToList();

        Assert.Equal(canonicalExit, aliasExit);
        Assert.Equal(canonicalStdout, aliasStdout);
        Assert.Equal(canonicalStderr, aliasStderr);
        Assert.Equal(canonicalRequests.Count, aliasRequests.Count);
        for (var i = 0; i < canonicalRequests.Count; i++)
        {
            Assert.Equal(canonicalRequests[i].Method, aliasRequests[i].Method);
            Assert.Equal(canonicalRequests[i].RequestUri, aliasRequests[i].RequestUri);
        }

        exit = aliasExit;
        stdoutText = aliasStdout;
        stderrText = aliasStderr;
    }

    [Fact]
    public async Task WorkflowHelp_ExposesReadVerbsAndNoYamlSubcommand()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowReadSetup();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workflow", "--help"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        foreach (var verb in new[] { "variables", "events", "list-sessions" })
        {
            Assert.Contains($"{verb} <run-id>", stdout);
        }
        // `get` is the canonical command and `show` is a transitional name
        // alias of it, so System.CommandLine surfaces both names on the
        // `get` row (e.g. `get, show <run-id>`).
        Assert.Contains("get, show <run-id>", stdout);
        var subcommandLines = stdout
            .Split('\n')
            .Where(line => line.StartsWith("  ", StringComparison.Ordinal))
            .ToList();
        Assert.DoesNotContain(subcommandLines, line => line.TrimStart().StartsWith("show ", StringComparison.Ordinal));
        // `status` is gone — the redundant command must not be discoverable.
        Assert.DoesNotContain(subcommandLines, line => line.TrimStart().StartsWith("status ", StringComparison.Ordinal));
        // `yaml` is an output format on `get`, never a stand-alone command.
        Assert.DoesNotContain("yaml <run-id>", stdout);
        Assert.DoesNotContain(subcommandLines, line => line.TrimStart().StartsWith("yaml ", StringComparison.Ordinal));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task WorkflowHelp_DoesNotExposeSingleSessionSubActions()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowReadSetup();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workflow", "--help"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("list-sessions <run-id>", stdout);
        Assert.DoesNotContain("session <name>", stdout);
        var subcommandLines = stdout
            .Split('\n')
            .Where(line => line.StartsWith("  ", StringComparison.Ordinal))
            .ToList();
        foreach (var verb in new[] { "transcript", "reset", "followup" })
        {
            Assert.DoesNotContain(subcommandLines, line => line.TrimStart().StartsWith($"{verb} ", StringComparison.Ordinal));
        }
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Get_IsTheCanonicalSingleResourceRead_HitsBareGetEndpointAndReturnsDetailPayload()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrId}")
                return RecordingHttpHandler.Json(new { success = true, data = SampleDetail() });
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workflow", "get", WrId, "--json", "status,issueRef"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var getReq = handler.Requests.Single(r => r.Method == HttpMethod.Get);
        Assert.Equal($"/api/workflow-runs/{WrId}", getReq.RequestUri?.PathAndQuery);
        var stdout = output.ToString();
        Assert.Contains("\"issueRef\"", stdout);
        Assert.Contains("\"number\": 42", stdout);
        Assert.Contains("Close the agent subscriptions gap", stdout);
    }

    [Fact]
    public async Task Get_DoesNotReverseResolveToIssueEndpoint()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrId}")
                return RecordingHttpHandler.Json(new { success = true, data = SampleDetail() });
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workflow", "get", WrId], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri?.PathAndQuery.Contains("/issues/") == true);
    }

    [Fact]
    public async Task Get_Table_Default_RendersSummaryIncludingStatusStageApprovalAndIssue()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrId}")
                return RecordingHttpHandler.Json(new { success = true, data = SampleDetail() });
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workflow", "get", WrId], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        // Default table renders the summary view: status, current stage,
        // assigned-to (when set), associated issue. The WorkflowRunDetail
        // renderer covers all four — see RenderWorkflowRunDetail.
        Assert.Contains("run id:", stdout);
        Assert.Contains(WrId, stdout);
        Assert.Contains("status:", stdout);
        Assert.Contains("current stage:", stdout);
        Assert.Contains("issue:", stdout);
        Assert.Contains("#42", stdout);
        Assert.Contains("Close the agent subscriptions gap", stdout);
    }

    [Fact]
    public async Task Get_Yaml_HitsYamlSubresourceEndpointAndPrintsDefinition()
    {
        var yamlBody = "name: mohist/local\nstages:\n  - id: plan\n  - id: build";
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrId}/yaml")
                return RecordingHttpHandler.Json(new { success = true, data = new { workflowRunId = WrId, yaml = yamlBody } });
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workflow", "get", WrId, "--yaml"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var getReq = handler.Requests.Single(r => r.Method == HttpMethod.Get);
        Assert.Equal($"/api/workflow-runs/{WrId}/yaml", getReq.RequestUri?.PathAndQuery);
        var stdout = output.ToString();
        Assert.Equal(yamlBody, stdout.TrimEnd());
    }

    [Fact]
    public async Task Get_Yaml_DoesNotHitBareGetEndpoint()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrId}/yaml")
                return RecordingHttpHandler.Json(new { success = true, data = new { workflowRunId = WrId, yaml = "name: mohist/local\n" } });
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workflow", "get", WrId, "--yaml"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain(handler.Requests, r =>
            r.Method == HttpMethod.Get && r.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrId}");
    }

    [Fact]
    public async Task GetHelp_DocumentsYamlOutputFormat()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowReadSetup();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workflow", "get", "--help"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("--yaml", stdout);
        Assert.Contains("--json", stdout);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Get_Yaml_OnMissingRun_SurfacesServerErrorToStderr()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get)
                return RecordingHttpHandler.JsonError(
                    $"Workflow run '{WrId}' not found",
                    code: "not_found",
                    statusCode: HttpStatusCode.NotFound);
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workflow", "get", WrId, "--yaml"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        var stderr = error.ToString();
        Assert.Contains("not found", stderr);
        Assert.Contains("not_found", stderr);
    }

    [Fact]
    public async Task Get_MissingRunId_FailsLocallyWithoutHttp()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowReadSetup();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workflow", "get"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("<run-id> is required", error.ToString());
    }

    // Alias parity: `show` runs through the same handler as `get`. We
    // re-execute the canonical bare-GET and the -o json|yaml paths under
    // both names with the same canned response and assert identical requests,
    // stdout, stderr, and exit codes. (Per spec scenario
    // workflow-run-reads/spec.md#show-behaves-identically-to-get.)
    [Fact]
    public async Task Show_IsAliasOfGet_BareJson_ProducesIdenticalRequestsOutputAndExit()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrId}")
                return RecordingHttpHandler.Json(new { success = true, data = SampleDetail() });
            return null!;
        });

        RunUnderBothNames(handler, http, output, error, fs, executor,
            new[] { WrId, "--json", "status,issueRef" },
            out var exit, out var stdout, out _);

        Assert.Equal(0, exit);
        Assert.Contains("\"issueRef\"", stdout);
    }

    [Fact]
    public async Task Show_IsAliasOfGet_BareTable_ProducesIdenticalRequestsOutputAndExit()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrId}")
                return RecordingHttpHandler.Json(new { success = true, data = SampleDetail() });
            return null!;
        });

        RunUnderBothNames(handler, http, output, error, fs, executor,
            new[] { WrId, },
            out var exit, out var stdout, out _);

        Assert.Equal(0, exit);
        Assert.Contains("run id:", stdout);
        Assert.Contains("issue:", stdout);
    }

    [Fact]
    public async Task Show_IsAliasOfGet_Yaml_HitsYamlSubresource_ProducesIdenticalRequestsOutputAndExit()
    {
        var yamlBody = "name: mohist/local\nstages:\n  - id: plan\n  - id: build";
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrId}/yaml")
                return RecordingHttpHandler.Json(new { success = true, data = new { workflowRunId = WrId, yaml = yamlBody } });
            return null!;
        });

        RunUnderBothNames(handler, http, output, error, fs, executor,
            new[] { WrId, "--yaml" },
            out var exit, out var stdout, out _);

        Assert.Equal(0, exit);
        // The yaml endpoint was hit, never the JSON read model.
        Assert.All(handler.Requests, r =>
        {
            if (r.Method == HttpMethod.Get)
                Assert.Equal($"/api/workflow-runs/{WrId}/yaml", r.RequestUri?.PathAndQuery);
        });
        Assert.Equal(yamlBody, stdout.TrimEnd());
    }

    [Fact]
    public async Task Show_Help_DocumentsYamlOutputFormat()
    {
        // The `show` alias surfaces the same help block as `get`.
        var (handler, http, output, error, fs, executor) = CreateWorkflowReadSetup();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workflow", "show", "--help"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("--yaml", stdout);
        Assert.Contains("--json", stdout);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Status_IsNotARegisteredSubcommand()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowReadSetup();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workflow", "status", WrId], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        // System.CommandLine surfaces an "Unrecognized command" / parse error
        // for an unknown subcommand. No request must have been issued.
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Status_IsNotAdvertisedInWorkflowHelp()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowReadSetup();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workflow", "--help"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        // No `status <run-id>` row in the subcommand table — the redundant
        // command must not be discoverable through `mo workflow --help`.
        Assert.DoesNotContain("status <run-id>", stdout);
        var subcommandLines = stdout
            .Split('\n')
            .Where(line => line.StartsWith("  ", StringComparison.Ordinal))
            .ToList();
        Assert.DoesNotContain(subcommandLines, line => line.TrimStart().StartsWith("status ", StringComparison.Ordinal));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Variables_HitsEffectiveSubresourceWithoutKeyOrStage()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrId}/variables/effective")
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new Dictionary<string, object>
                    {
                        ["foo"] = "bar",
                        ["count"] = 3,
                    },
                });
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workflow", "variables", WrId], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var getReq = handler.Requests.Single(r => r.Method == HttpMethod.Get);
        Assert.Equal($"/api/workflow-runs/{WrId}/variables/effective", getReq.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task Variables_WithStage_AppendsStageQueryParam()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrId}/variables/effective?stage=plan")
                return RecordingHttpHandler.Json(new { success = true, data = new Dictionary<string, object>() });
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workflow", "variables", WrId, "--stage", "plan"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var getReq = handler.Requests.Single(r => r.Method == HttpMethod.Get);
        Assert.Equal($"/api/workflow-runs/{WrId}/variables/effective?stage=plan", getReq.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task Variables_WithKey_AppendsKeyPathToSubresource()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrId}/variables/effective/some.nested.key")
                return RecordingHttpHandler.Json(new { success = true, data = "the-value" });
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workflow", "variables", WrId, "--key", "some.nested.key"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var getReq = handler.Requests.Single(r => r.Method == HttpMethod.Get);
        Assert.Equal($"/api/workflow-runs/{WrId}/variables/effective/some.nested.key", getReq.RequestUri?.PathAndQuery);
        var stdout = output.ToString();
        Assert.Contains("the-value", stdout);
    }

    [Fact]
    public async Task Variables_WithKeyAndStage_AppendsKeyPathAndStageQueryParam()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrId}/variables/effective/some.nested.key?stage=plan")
                return RecordingHttpHandler.Json(new { success = true, data = "scoped-value" });
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workflow", "variables", WrId, "--key", "some.nested.key", "--stage", "plan"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var getReq = handler.Requests.Single(r => r.Method == HttpMethod.Get);
        Assert.Equal($"/api/workflow-runs/{WrId}/variables/effective/some.nested.key?stage=plan", getReq.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task Events_HitsRunScopedEventsEndpoint()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrId}/events")
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new[]
                    {
                        new
                        {
                            id = 1L,
                            eventId = "evt-1",
                            source = "/api/workflow-runs/" + WrId,
                            type = "io.mohist.workflow.stage.started",
                            specVersion = "1.0",
                            subject = (string?)null,
                            time = "2026-07-05T01:00:00Z",
                            dataContentType = (string?)null,
                            data = new { },
                            extensions = new Dictionary<string, string>(),
                        },
                    },
                });
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workflow", "events", WrId, "--json", "id"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var getReq = handler.Requests.Single(r => r.Method == HttpMethod.Get);
        Assert.Equal($"/api/workflow-runs/{WrId}/events", getReq.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task Events_WithLimit_AppendsLimitQueryParam()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrId}/events?limit=50")
                return RecordingHttpHandler.Json(new { success = true, data = Array.Empty<object>() });
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workflow", "events", WrId, "--limit", "50", "--json", "id"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var getReq = handler.Requests.Single(r => r.Method == HttpMethod.Get);
        Assert.Equal($"/api/workflow-runs/{WrId}/events?limit=50", getReq.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task ListSessions_HitsRunScopedSessionsEndpoint()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrId}/sessions")
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new[]
                    {
                        new
                        {
                            sessionName = "build",
                            workflowRunId = WrId,
                            status = "running",
                            agentKind = "default",
                            createdAt = "2026-07-05T01:00:00Z",
                            lastActivityAt = (string?)null,
                            contextUsagePercent = (double?)null,
                        },
                    },
                });
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workflow", "list-sessions", WrId, "--json", "id"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var getReq = handler.Requests.Single(r => r.Method == HttpMethod.Get);
        Assert.Equal($"/api/workflow-runs/{WrId}/sessions", getReq.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task ListSessions_DoesNotExposeSingleSessionSubActions()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowReadSetup();

        foreach (var verb in new[] { "get", "transcript", "compact", "reset", "followup" })
        {
            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();
            var exitCode = await MohistCliCommands.RunAsync(
                http, ["workflow", "session", verb, WrId, "build", "--json", "id"], output, error, fs, executor);
            Assert.NotEqual(0, exitCode);
            Assert.Empty(handler.Requests);
        }
    }

    [Fact]
    public async Task ReadCommands_AddressByRunIdOnly_NoProjectOrIssueRequired()
    {
        // No active project on disk and no --project/--project-id flag — yet
        // every read verb resolves the run solely from the workflowRunId and
        // issues its GET. The status subcommand is gone and not exercised
        // here.
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(responder: null, activeProjectId: null);
        handler.SetResponder((req, _) =>
        {
            if (req.Method == HttpMethod.Get
                && (req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrId}"
                    || req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrId}/yaml"
                    || req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrId}/variables/effective"
                    || req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrId}/events"
                    || req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrId}/sessions"))
                return Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = new { } }));
            return Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = new { } }));
        });

        foreach (var args in new[]
        {
            new[] { "workflow", "get", WrId },
            new[] { "workflow", "show", WrId },
            new[] { "workflow", "variables", WrId },
            new[] { "workflow", "events", WrId },
            new[] { "workflow", "list-sessions", WrId },
        })
        {
            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();
            var exitCode = await MohistCliCommands.RunAsync(
                http, args, output, error, fs, executor);
            Assert.Equal(0, exitCode);
        }

        Assert.DoesNotContain(handler.Requests, r => r.RequestUri?.PathAndQuery.Contains("/issues/") == true);
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri?.PathAndQuery.Contains("/projects/") == true);
    }

    [Fact]
    public async Task Get_UnknownRunId_PrintsServerErrorToStderrAndExitsNonZero()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrId}")
                return RecordingHttpHandler.JsonError(
                    $"Workflow run '{WrId}' not found",
                    code: "not_found",
                    statusCode: HttpStatusCode.NotFound);
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workflow", "get", WrId], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        var stderr = error.ToString();
        Assert.Contains("not found", stderr);
        Assert.Contains("not_found", stderr);
    }

    [Fact]
    public async Task Show_UnknownRunId_PrintsServerErrorToStderrAndExitsNonZero()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrId}")
                return RecordingHttpHandler.JsonError(
                    $"Workflow run '{WrId}' not found",
                    code: "not_found",
                    statusCode: HttpStatusCode.NotFound);
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workflow", "show", WrId], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("not found", error.ToString());
        Assert.Contains("not_found", error.ToString());
    }

    [Fact]
    public async Task Variables_UnknownRunId_PrintsServerErrorToStderrAndExitsNonZero()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get)
                return RecordingHttpHandler.JsonError(
                    $"Workflow run '{WrId}' not found",
                    code: "not_found",
                    statusCode: HttpStatusCode.NotFound);
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workflow", "variables", WrId], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("not found", error.ToString());
    }

    [Fact]
    public async Task Events_UnknownRunId_PrintsServerErrorToStderrAndExitsNonZero()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get)
                return RecordingHttpHandler.JsonError(
                    $"Workflow run '{WrId}' not found",
                    code: "not_found",
                    statusCode: HttpStatusCode.NotFound);
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workflow", "events", WrId], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("not found", error.ToString());
    }

    [Fact]
    public async Task ListSessions_UnknownRunId_PrintsServerErrorToStderrAndExitsNonZero()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get)
                return RecordingHttpHandler.JsonError(
                    $"Workflow run '{WrId}' not found",
                    code: "not_found",
                    statusCode: HttpStatusCode.NotFound);
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workflow", "list-sessions", WrId], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("not found", error.ToString());
    }

    [Fact]
    public async Task Get_InvalidOutputFormat_FailsBeforeHttp()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowReadSetup();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workflow", "get", WrId, "-o", "xml"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("--json", error.ToString());
    }
}
