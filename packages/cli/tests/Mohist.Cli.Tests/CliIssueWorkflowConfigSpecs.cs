using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public class CliIssueWorkflowConfigSpecs
{
    private static (RecordingHttpHandler Handler, HttpClient Http, StringWriter Output, StringWriter Error, FakeFileSystem Fs, FakeCommandExecutor Executor)
        CreateWorkflowConfigSetup(Func<HttpRequestMessage, HttpResponseMessage>? responder = null)
    {
        return CliTestFactory.Create(
            responder is null ? null : (Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>)((req, _) => Task.FromResult(responder(req))));
    }

    private static (RecordingHttpHandler Handler, HttpClient Http, StringWriter Output, StringWriter Error, FakeFileSystem Fs, FakeCommandExecutor Executor)
        CreateAsyncWorkflowConfigSetup(Func<HttpRequestMessage, Task<HttpResponseMessage>>? responder = null)
    {
        return CliTestFactory.Create(
            responder is null ? null : (Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>)((req, _) => responder(req)));
    }

    private static void AssertVariableSetToJsonNull(JsonObject? body, string varsKey, string expectedKey)
    {
        Assert.NotNull(body);
        var section = body![varsKey] as JsonObject;
        Assert.NotNull(section);
        var found = false;
        foreach (var kvp in section!)
        {
            if (!string.Equals(kvp.Key, expectedKey, StringComparison.Ordinal)) continue;
            found = true;
            Assert.Null(kvp.Value);
        }
        Assert.True(found, $"expected key '{expectedKey}' to be present in '{varsKey}' with JSON null value");
    }

    private static object SampleProfile() => new
    {
        issueNumber = 42,
        projectId = "proj_abc",
        sourceTemplateId = "mohist/local",
        hasCustomTemplate = true,
        yaml = "name: workflow\ntasks:\n  - stage: plan\n    with:\n      greeting: hi\n",
        workflowRunId = (string?)null,
        profileId = "mohist/local",
        updateMode = "custom",
        templateSource = "custom",
        variables = new
        {
            vars = new Dictionary<string, object>
            {
                ["foo"] = "bar",
                ["nested"] = new Dictionary<string, object> { ["k"] = "v" },
            },
            stages = new Dictionary<string, object>
            {
                ["plan"] = new { vars = new Dictionary<string, object> { ["baz"] = "qux" } },
            },
        },
        updatedAt = "2026-06-26T00:00:00Z",
    };

    [Fact]
    public async Task ConfigHelp_ListsThreeSubcommandsWithoutPromptCommands()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup();
        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "workflow", "config", "--help"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("get", stdout);
        Assert.Contains("set", stdout);
        Assert.Contains("clear", stdout);
        Assert.DoesNotContain("preview", stdout);
        Assert.DoesNotContain("--prompt", stdout);
    }

    [Fact]
    public async Task ConfigGet_TableMode_SendsGetRequestAndRendersTemplateAndVariables()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42/workflow-profile")
            {
                return RecordingHttpHandler.Json(new { success = true, data = SampleProfile() });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "workflow", "config", "get", "42", "-o", "table"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var getReqs = handler.Requests.Where(r => r.Method == HttpMethod.Get).ToList();
        Assert.Contains(getReqs, r => r.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42/workflow-profile");
        var stdout = output.ToString();
        Assert.Contains("template:", stdout);
        Assert.Contains("variables:", stdout);
        Assert.Contains("name: workflow", stdout);
        Assert.Contains("foo", stdout);
    }

    [Fact]
    public async Task ConfigGet_JsonMode_EmitsProfileWithoutPrompts()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42/workflow-profile")
                return RecordingHttpHandler.Json(new { success = true, data = SampleProfile() });
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "workflow", "config", "get", "42", "-o", "json"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("\"issueNumber\":", stdout);
        Assert.Contains("\"profileId\":", stdout);
        Assert.DoesNotContain("\"prompts\":", stdout);
        var getReqs = handler.Requests.Where(r => r.Method == HttpMethod.Get).ToList();
        Assert.Single(getReqs);
        Assert.Equal("/api/projects/proj_abc/issues/42/workflow-profile", getReqs[0].RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task ConfigGet_AcceptsProjectAndProjectIdAlias()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup(req =>
        {
            if (req.Method == HttpMethod.Get)
                return RecordingHttpHandler.Json(new { success = true, data = SampleProfile() });
            return null!;
        });

        var byName = await MohistCliCommands.RunAsync(
            http, ["issue", "workflow", "config", "get", "42", "--project", "proj_abc"], output, error, fs, executor);
        Assert.Equal(0, byName);
        Assert.Contains(handler.Requests.Where(r => r.Method == HttpMethod.Get),
            r => r.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42/workflow-profile");

        var byId = await MohistCliCommands.RunAsync(
            http, ["issue", "workflow", "config", "get", "42", "--project-id", "proj_abc"], output, error, fs, executor);
        Assert.Equal(0, byId);
        Assert.Contains(handler.Requests.Where(r => r.Method == HttpMethod.Get),
            r => r.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42/workflow-profile");
    }

    [Fact]
    public async Task ConfigGet_InvalidOutputMode_PrintsErrorAndExitsOne()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "workflow", "config", "get", "42", "-o", "yaml"], output, error, fs, executor);

        Assert.Equal(1, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("table", error.ToString());
        Assert.Contains("json", error.ToString());
    }

    [Fact]
    public async Task ConfigGet_ServerError_SurfacesErrorAndExitsNonZero()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup(req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return RecordingHttpHandler.JsonError(
                    "Issue 42 not found",
                    code: "issue_not_found",
                    statusCode: HttpStatusCode.NotFound);
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "workflow", "config", "get", "42"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Issue 42 not found", error.ToString());
    }

    [Fact]
    public async Task ConfigGet_TableMode_ServerErrorOnProfile_SurfacesErrorAndExitsNonZero()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42/workflow-profile")
            {
                return RecordingHttpHandler.JsonError(
                    "Issue 42 not found",
                    code: "issue_not_found",
                    statusCode: HttpStatusCode.NotFound);
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "workflow", "config", "get", "42", "-o", "table"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Issue 42 not found", error.ToString());
        var getReqs = handler.Requests.Where(r => r.Method == HttpMethod.Get).ToList();
        Assert.Single(getReqs);
    }

    [Fact]
    public async Task ConfigSet_NoFlags_MakesNoRequestAndExitsNonZero()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "workflow", "config", "set", "42"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
        var errText = error.ToString();
        Assert.Contains("nothing to change", errText);
    }

    [Fact]
    public async Task ConfigSet_TemplateAtFile_ReadsFileAndPutsTemplate()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup(req =>
        {
            if (req.Method == HttpMethod.Put && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42/workflow-profile/template")
            {
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            }
            return null!;
        });
        fs.AddFile("/wf.yaml", "name: workflow\ntasks: []\n");

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "workflow", "config", "set", "42", "--template", "@/wf.yaml"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var putReq = handler.Requests.Single(r => r.Method == HttpMethod.Put);
        Assert.Equal("/api/projects/proj_abc/issues/42/workflow-profile/template", putReq.RequestUri?.PathAndQuery);
        var body = JsonNode.Parse(putReq.Body!) as JsonObject;
        Assert.NotNull(body);
        Assert.Equal("name: workflow\ntasks: []\n", body["yaml"]?.GetValue<string>());
    }

    [Fact]
    public async Task ConfigSet_TemplateInline_PutsYamlAsBody()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup(req =>
        {
            if (req.Method == HttpMethod.Put && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42/workflow-profile/template")
            {
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "workflow", "config", "set", "42", "--template", "name: inline"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var putReq = handler.Requests.Single(r => r.Method == HttpMethod.Put);
        var body = JsonNode.Parse(putReq.Body!) as JsonObject;
        Assert.Equal("name: inline", body!["yaml"]?.GetValue<string>());
    }

    [Fact]
    public async Task ConfigSet_VarAndStageVar_PatchMergesIntoSingleBody()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup(req =>
        {
            if (req.Method == HttpMethod.Patch && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42/workflow-profile/variables")
            {
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["issue", "workflow", "config", "set", "42",
                "--var", "foo=bar",
                "--var", "qux=quux",
                "--stage-var", "plan.baz=qux",
                "--stage-var", "build.things=a=b=c"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var patchReq = handler.Requests.Single(r => r.Method == HttpMethod.Patch);
        Assert.Equal("/api/projects/proj_abc/issues/42/workflow-profile/variables", patchReq.RequestUri?.PathAndQuery);
        var body = JsonNode.Parse(patchReq.Body!) as JsonObject;
        Assert.NotNull(body);
        var vars = body!["vars"] as JsonObject;
        Assert.Equal("bar", vars!["foo"]?.GetValue<string>());
        Assert.Equal("quux", vars["qux"]?.GetValue<string>());
        var stages = body["stages"] as JsonObject;
        Assert.NotNull(stages);
        var plan = stages!["plan"] as JsonObject;
        var planVars = plan!["vars"] as JsonObject;
        Assert.Equal("qux", planVars!["baz"]?.GetValue<string>());
        var build = stages["build"] as JsonObject;
        var buildVars = build!["vars"] as JsonObject;
        Assert.Equal("a=b=c", buildVars!["things"]?.GetValue<string>());
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task ConfigSet_OnlyVar_OmitsStagesSection()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup(req =>
        {
            if (req.Method == HttpMethod.Patch && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42/workflow-profile/variables")
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "workflow", "config", "set", "42", "--var", "foo=bar"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var patchReq = handler.Requests.Single(r => r.Method == HttpMethod.Patch);
        var body = JsonNode.Parse(patchReq.Body!) as JsonObject;
        Assert.NotNull(body!["vars"]);
        Assert.False(body.ContainsKey("stages"));
    }

    [Fact]
    public async Task ConfigSet_OnlyStageVar_OmitsVarsSection()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup(req =>
        {
            if (req.Method == HttpMethod.Patch && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42/workflow-profile/variables")
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "workflow", "config", "set", "42", "--stage-var", "plan.baz=qux"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var patchReq = handler.Requests.Single(r => r.Method == HttpMethod.Patch);
        var body = JsonNode.Parse(patchReq.Body!) as JsonObject;
        Assert.False(body!.ContainsKey("vars"));
        var stages = body["stages"] as JsonObject;
        var plan = stages!["plan"] as JsonObject;
        var planVars = plan!["vars"] as JsonObject;
        Assert.Equal("qux", planVars!["baz"]?.GetValue<string>());
    }

    [Fact]
    public async Task ConfigSet_Var_TableMode_RendersVariableBundle()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup(req =>
        {
            if (req.Method == HttpMethod.Patch && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42/workflow-profile/variables")
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new
                    {
                        vars = new Dictionary<string, object> { ["foo"] = "bar" },
                        stages = new Dictionary<string, object>
                        {
                            ["plan"] = new { vars = new Dictionary<string, object> { ["baz"] = "qux" } }
                        }
                    }
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "workflow", "config", "set", "42", "--var", "foo=bar", "-o", "table"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("variables:", stdout);
        Assert.Contains("foo: bar", stdout);
        Assert.Contains("plan", stdout);
        Assert.Contains("baz: qux", stdout);
        Assert.DoesNotContain("template:", stdout);
        Assert.DoesNotContain("issue:", stdout);
    }

    [Fact]
    public async Task ConfigSetAndClear_StageVar_UseSameServerShape()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup(req =>
        {
            if (req.Method == HttpMethod.Patch && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42/workflow-profile/variables")
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            return null!;
        });

        var setExitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "workflow", "config", "set", "42", "--stage-var", "plan.baz=qux"], output, error, fs, executor);
        var clearExitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "workflow", "config", "clear", "42", "--var", "plan.baz"], output, error, fs, executor);

        Assert.Equal(0, setExitCode);
        Assert.Equal(0, clearExitCode);
        var patchReqs = handler.Requests.Where(r => r.Method == HttpMethod.Patch).ToList();
        Assert.Equal(2, patchReqs.Count);

        var setBody = JsonNode.Parse(patchReqs[0].Body!) as JsonObject;
        var setPlan = setBody!["stages"]!["plan"] as JsonObject;
        var setPlanVars = setPlan!["vars"] as JsonObject;
        Assert.Equal("qux", setPlanVars!["baz"]?.GetValue<string>());

        var clearBody = JsonNode.Parse(patchReqs[1].Body!) as JsonObject;
        var clearPlan = clearBody!["stages"]!["plan"] as JsonObject;
        AssertVariableSetToJsonNull(clearPlan, "vars", "baz");
    }

    [Fact]
    public async Task ConfigSet_Composite_IssuesTemplateAndVariablesRequests()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup(req =>
        {
            if (req.Method == HttpMethod.Put || req.Method == HttpMethod.Patch)
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            return null!;
        });
        fs.AddFile("/wf.yaml", "name: composite\n");

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["issue", "workflow", "config", "set", "42",
                "--template", "@/wf.yaml",
                "--var", "foo=bar"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var puts = handler.Requests.Where(r => r.Method == HttpMethod.Put).ToList();
        var patches = handler.Requests.Where(r => r.Method == HttpMethod.Patch).ToList();
        Assert.Single(puts);
        Assert.Single(patches);
        Assert.Contains(puts, p => p.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42/workflow-profile/template");
        Assert.Equal("/api/projects/proj_abc/issues/42/workflow-profile/variables", patches[0].RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task ConfigSet_MalformedVarNoEquals_PrintsErrorAndExitsNonZero()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "workflow", "config", "set", "42", "--var", "novalue"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("k=v", error.ToString());
    }

    [Fact]
    public async Task ConfigSet_MalformedStageVarNoDot_PrintsErrorAndExitsNonZero()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "workflow", "config", "set", "42", "--stage-var", "no-dot"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("<stage>.k=v", error.ToString());
    }

    [Fact]
    public async Task ConfigSet_TemplateAtFileMissing_PrintsErrorAndExitsNonZero()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "workflow", "config", "set", "42", "--template", "@/missing.yaml"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("could not read file", error.ToString());
    }

    [Fact]
    public async Task ConfigSet_ServerRejectsTemplate_SurfacesErrorAndExitsNonZero()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup(req =>
        {
            if (req.Method == HttpMethod.Put && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42/workflow-profile/template")
            {
                return RecordingHttpHandler.JsonError(
                    "YAML syntax error: bad indent",
                    code: "yaml_syntax",
                    statusCode: HttpStatusCode.BadRequest);
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "workflow", "config", "set", "42", "--template", "name: : bad"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("YAML syntax error", error.ToString());
    }

    [Fact]
    public async Task ConfigSet_InvalidOutputMode_PrintsErrorAndExitsNonZero()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "workflow", "config", "set", "42", "--template", "x", "-o", "yaml"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("table", error.ToString());
        Assert.Contains("json", error.ToString());
    }

    [Fact]
    public async Task ConfigClear_NoFlags_MakesNoRequestAndExitsNonZero()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "workflow", "config", "clear", "42"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("nothing to clear", error.ToString());
    }

    [Fact]
    public async Task ConfigClear_Template_DeletesTemplateOverride()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup(req =>
        {
            if (req.Method == HttpMethod.Delete && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42/workflow-profile/template")
            {
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "workflow", "config", "clear", "42", "--template"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var deleteReq = handler.Requests.Single(r => r.Method == HttpMethod.Delete);
        Assert.Equal("/api/projects/proj_abc/issues/42/workflow-profile/template", deleteReq.RequestUri?.PathAndQuery);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Patch);
    }

    [Fact]
    public async Task ConfigClear_Var_PatchesVariableToJsonNull()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup(req =>
        {
            if (req.Method == HttpMethod.Patch && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42/workflow-profile/variables")
            {
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "workflow", "config", "clear", "42", "--var", "foo"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var patchReq = handler.Requests.Single(r => r.Method == HttpMethod.Patch);
        Assert.Equal("/api/projects/proj_abc/issues/42/workflow-profile/variables", patchReq.RequestUri?.PathAndQuery);
        var body = JsonNode.Parse(patchReq.Body!) as JsonObject;
        Assert.NotNull(body);
        AssertVariableSetToJsonNull(body, "vars", "foo");
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task ConfigClear_StageVar_PatchesStageVariableToJsonNull()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup(req =>
        {
            if (req.Method == HttpMethod.Patch && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42/workflow-profile/variables")
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "workflow", "config", "clear", "42", "--var", "plan.baz"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var patchReq = handler.Requests.Single(r => r.Method == HttpMethod.Patch);
        var body = JsonNode.Parse(patchReq.Body!) as JsonObject;
        Assert.False(body!.ContainsKey("vars"));
        var stages = body["stages"] as JsonObject;
        Assert.NotNull(stages);
        AssertVariableSetToJsonNull(stages!["plan"] as JsonObject, "vars", "baz");
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Delete || r.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task ConfigClear_TopLevelAndStageVars_MergeIntoSinglePatch()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup(req =>
        {
            if (req.Method == HttpMethod.Patch && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42/workflow-profile/variables")
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["issue", "workflow", "config", "clear", "42",
                "--var", "foo",
                "--var", "plan.baz"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var patchReq = handler.Requests.Single(r => r.Method == HttpMethod.Patch);
        var body = JsonNode.Parse(patchReq.Body!) as JsonObject;
        AssertVariableSetToJsonNull(body, "vars", "foo");
        var stages = body!["stages"] as JsonObject;
        AssertVariableSetToJsonNull(stages!["plan"] as JsonObject, "vars", "baz");
    }

    [Fact]
    public async Task ConfigClear_Var_FakeHandlerAssertsNullPayload_RoundTripsAfterServerRemoval()
    {
        var capturedBodies = new List<string>();
        var storedVars = new Dictionary<string, object?>(StringComparer.Ordinal) { ["foo"] = "bar", ["other"] = "kept" };

        var (handler, http, output, error, fs, executor) = CreateAsyncWorkflowConfigSetup(async req =>
        {
            if (req.Method == HttpMethod.Patch && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42/workflow-profile/variables")
            {
                var bodyText = req.Content is null ? null : await req.Content.ReadAsStringAsync();
                capturedBodies.Add(bodyText ?? string.Empty);
                var body = JsonNode.Parse(bodyText ?? "{}") as JsonObject;
                var overlay = body?["vars"] as JsonObject;
                if (overlay is not null)
                {
                    foreach (var kvp in overlay)
                    {
                        if (kvp.Value is null || kvp.Value.GetValueKind() == JsonValueKind.Null)
                            storedVars.Remove(kvp.Key);
                        else
                            storedVars[kvp.Key] = kvp.Value.GetValueKind() == JsonValueKind.String
                                ? kvp.Value.GetValue<string>()
                                : kvp.Value.ToJsonString();
                    }
                }
                return RecordingHttpHandler.Json(new { success = true, data = new { vars = storedVars } });
            }
            return RecordingHttpHandler.Json(new { success = true, data = new { } });
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "workflow", "config", "clear", "42", "--var", "foo"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Single(capturedBodies);
        Assert.Contains("\"foo\"", capturedBodies[0]);
        Assert.Matches("\"foo\"\\s*:\\s*null", capturedBodies[0]);
        Assert.DoesNotContain("foo", storedVars.Keys);
        Assert.Contains("other", storedVars.Keys);
    }

    [Fact]
    public async Task ConfigClear_Var_TableMode_RendersVariableBundle()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup(req =>
        {
            if (req.Method == HttpMethod.Patch && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42/workflow-profile/variables")
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new
                    {
                        vars = new Dictionary<string, object> { ["other"] = "kept" },
                        stages = new Dictionary<string, object>()
                    }
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "workflow", "config", "clear", "42", "--var", "foo", "-o", "table"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("variables:", stdout);
        Assert.Contains("other: kept", stdout);
        Assert.DoesNotContain("foo:", stdout);
        Assert.DoesNotContain("template:", stdout);
        Assert.DoesNotContain("issue:", stdout);
    }

    [Fact]
    public async Task ConfigClear_VarAndTemplate_IssuesBoth()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup(req =>
        {
            if (req.Method == HttpMethod.Patch || req.Method == HttpMethod.Delete)
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["issue", "workflow", "config", "clear", "42",
                "--var", "foo",
                "--template"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var patchReq = handler.Requests.Single(r => r.Method == HttpMethod.Patch);
        Assert.Equal("/api/projects/proj_abc/issues/42/workflow-profile/variables", patchReq.RequestUri?.PathAndQuery);

        var deleteReq = handler.Requests.Single(r => r.Method == HttpMethod.Delete);
        Assert.Equal("/api/projects/proj_abc/issues/42/workflow-profile/template", deleteReq.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task ConfigClear_MultipleVars_MergeIntoSinglePatchWithAllKeysSetToNull()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup(req =>
        {
            if (req.Method == HttpMethod.Patch && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42/workflow-profile/variables")
            {
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["issue", "workflow", "config", "clear", "42",
                "--var", "foo",
                "--var", "bar",
                "--var", "baz"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var patchReqs = handler.Requests.Where(r => r.Method == HttpMethod.Patch).ToList();
        Assert.Single(patchReqs);
        var body = JsonNode.Parse(patchReqs[0].Body!) as JsonObject;
        var vars = body!["vars"] as JsonObject;
        Assert.NotNull(vars);
        var foundKeys = vars!.Select(kvp => kvp.Key).ToHashSet();
        Assert.Equal(3, foundKeys.Count);
        Assert.Contains("foo", foundKeys);
        Assert.Contains("bar", foundKeys);
        Assert.Contains("baz", foundKeys);
        foreach (var kvp in vars!)
        {
            Assert.Null(kvp.Value);
        }
    }

    [Fact]
    public async Task ConfigClear_Var_OnlyAffectsVars_OtherEndpointsUnchanged()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup(req =>
        {
            if (req.Method == HttpMethod.Patch)
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "workflow", "config", "clear", "42", "--var", "foo"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Single(handler.Requests);
        var patchReq = handler.Requests.Single();
        Assert.Equal(HttpMethod.Patch, patchReq.Method);
        var body = JsonNode.Parse(patchReq.Body!) as JsonObject;
        Assert.NotNull(body!["vars"]);
        Assert.False(body.ContainsKey("stages"));
    }

    [Fact]
    public async Task ConfigClear_EmptyVarKey_PrintsErrorAndExitsNonZero()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "workflow", "config", "clear", "42", "--var", "  "], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("empty key", error.ToString());
    }

    [Fact]
    public async Task ConfigClear_ServerRejectsVarPatch_SurfacesErrorAndExitsNonZero()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup(req =>
        {
            if (req.Method == HttpMethod.Patch)
            {
                return RecordingHttpHandler.JsonError(
                    "Cannot clear missing variable 'foo'",
                    code: "variable_not_found",
                    statusCode: HttpStatusCode.BadRequest);
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "workflow", "config", "clear", "42", "--var", "foo"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Cannot clear missing variable", error.ToString());
    }

    [Fact]
    public async Task ConfigClear_ServerRejectsTemplateDelete_SurfacesErrorAndExitsNonZero()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup(req =>
        {
            if (req.Method == HttpMethod.Delete)
            {
                return RecordingHttpHandler.JsonError(
                    "Template cannot be removed while a run is in progress",
                    code: "template_locked",
                    statusCode: HttpStatusCode.Conflict);
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "workflow", "config", "clear", "42", "--template"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Template cannot be removed", error.ToString());
    }

    [Fact]
    public async Task ConfigClear_InvalidOutputMode_PrintsErrorAndExitsNonZero()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["issue", "workflow", "config", "clear", "42", "--template", "-o", "yaml"],
            output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("table", error.ToString());
        Assert.Contains("json", error.ToString());
    }
}
