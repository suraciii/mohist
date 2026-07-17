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

    private static object SampleProfile(bool includePrompts = true) => new
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
        prompts = includePrompts ? new Dictionary<string, string>
        {
            ["greeting"] = "Hello {{ name }}!",
            ["plan_prompt"] = "Plan with {{ foo }}.",
        } : null,
        updatedAt = "2026-06-26T00:00:00Z",
    };

    [Fact]
    public async Task ConfigHelp_ListsFourSubcommands()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup();
        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "workflow", "config", "--help"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("get", stdout);
        Assert.Contains("set", stdout);
        Assert.Contains("clear", stdout);
        Assert.Contains("preview", stdout);
    }

    [Fact]
    public async Task ConfigGet_TableMode_SendsGetRequestAndRendersThreeSections()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42/workflow-profile")
            {
                return RecordingHttpHandler.Json(new { success = true, data = SampleProfile() });
            }
            if (req.Method == HttpMethod.Get && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42/workflow-profile/prompts")
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new Dictionary<string, string>
                    {
                        ["greeting"] = "Hello {{ name }}!",
                        ["plan_prompt"] = "Plan with {{ foo }}.",
                    },
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "workflow", "config", "get", "42", "-o", "table"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var getReqs = handler.Requests.Where(r => r.Method == HttpMethod.Get).ToList();
        Assert.Contains(getReqs, r => r.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42/workflow-profile");
        Assert.Contains(getReqs, r => r.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42/workflow-profile/prompts");
        var stdout = output.ToString();
        Assert.Contains("template:", stdout);
        Assert.Contains("variables:", stdout);
        Assert.Contains("prompts:", stdout);
        Assert.Contains("name: workflow", stdout);
        Assert.Contains("foo", stdout);
        Assert.Contains("plan_prompt", stdout);
        Assert.Contains("Hello {{ name }}!", stdout);
    }

    [Fact]
    public async Task ConfigGet_TableMode_FetchesProfileThenPrompts()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42/workflow-profile")
            {
                return RecordingHttpHandler.Json(new { success = true, data = SampleProfile() });
            }
            if (req.Method == HttpMethod.Get && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42/workflow-profile/prompts")
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new Dictionary<string, string>(),
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "workflow", "config", "get", "42", "-o", "table"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var getReqs = handler.Requests.Where(r => r.Method == HttpMethod.Get).ToList();
        Assert.Equal(2, getReqs.Count);
        Assert.Equal("/api/projects/proj_abc/issues/42/workflow-profile", getReqs[0].RequestUri?.PathAndQuery);
        Assert.Equal("/api/projects/proj_abc/issues/42/workflow-profile/prompts", getReqs[1].RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task ConfigGet_JsonMode_EmitsProfileWithPrompts()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42/workflow-profile")
                return RecordingHttpHandler.Json(new { success = true, data = SampleProfile(includePrompts: false) });
            if (req.Method == HttpMethod.Get && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42/workflow-profile/prompts")
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new Dictionary<string, string>
                    {
                        ["greeting"] = "Hello {{ name }}!",
                        ["plan_prompt"] = "Plan with {{ foo }}.",
                    },
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "workflow", "config", "get", "42", "-o", "json"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("\"issueNumber\":", stdout);
        Assert.Contains("\"profileId\":", stdout);
        Assert.Contains("\"prompts\":", stdout);
        Assert.Contains("\"plan_prompt\":", stdout);
        var getReqs = handler.Requests.Where(r => r.Method == HttpMethod.Get).ToList();
        Assert.Equal(2, getReqs.Count);
        Assert.Equal("/api/projects/proj_abc/issues/42/workflow-profile", getReqs[0].RequestUri?.PathAndQuery);
        Assert.Equal("/api/projects/proj_abc/issues/42/workflow-profile/prompts", getReqs[1].RequestUri?.PathAndQuery);
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
    public async Task ConfigGet_TableMode_ServerErrorOnPrompts_SurfacesErrorAndExitsNonZero()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42/workflow-profile")
                return RecordingHttpHandler.Json(new { success = true, data = SampleProfile(includePrompts: false) });
            if (req.Method == HttpMethod.Get && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42/workflow-profile/prompts")
            {
                return RecordingHttpHandler.JsonError(
                    "Prompt list unavailable",
                    code: "prompt_list_failed",
                    statusCode: HttpStatusCode.InternalServerError);
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "workflow", "config", "get", "42", "-o", "table"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Prompt list unavailable", error.ToString());
        Assert.Equal(2, handler.Requests.Count(r => r.Method == HttpMethod.Get));
    }

    [Fact]
    public async Task ConfigPreview_TableMode_SendsPostAndPrintsRenderedText()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup(req =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42/workflow-profile/prompts/plan_prompt/preview")
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new
                    {
                        rendered = "Plan with bar.",
                        missing = new string[] { },
                        depth = 1,
                    },
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "workflow", "config", "preview", "42", "plan_prompt", "-o", "table"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var postReq = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        Assert.Equal("/api/projects/proj_abc/issues/42/workflow-profile/prompts/plan_prompt/preview", postReq.RequestUri?.PathAndQuery);
        Assert.Equal("Plan with bar.", output.ToString().Trim());
    }

    [Fact]
    public async Task ConfigPreview_JsonMode_EmitsRawPayload()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup(req =>
        {
            if (req.Method == HttpMethod.Post)
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new
                    {
                        rendered = "Plan with bar.",
                        missing = new string[] { },
                        depth = 1,
                    },
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "workflow", "config", "preview", "42", "plan_prompt", "-o", "json"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("\"rendered\":", stdout);
        Assert.Contains("Plan with bar.", stdout);
    }

    [Fact]
    public async Task ConfigPreview_AcceptsProjectAndProjectIdAlias()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup(req =>
        {
            if (req.Method == HttpMethod.Post)
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new { rendered = "x", missing = new string[] { }, depth = 0 },
                });
            return null!;
        });

        var byName = await MohistCliCommands.RunAsync(
            http, ["issue", "workflow", "config", "preview", "42", "plan_prompt", "--project", "proj_abc"], output, error, fs, executor);
        Assert.Equal(0, byName);
        var postReq1 = handler.Requests.Last(r => r.Method == HttpMethod.Post);
        Assert.Equal("/api/projects/proj_abc/issues/42/workflow-profile/prompts/plan_prompt/preview", postReq1.RequestUri?.PathAndQuery);

        var byId = await MohistCliCommands.RunAsync(
            http, ["issue", "workflow", "config", "preview", "42", "plan_prompt", "--project-id", "proj_abc"], output, error, fs, executor);
        Assert.Equal(0, byId);
        var postReq2 = handler.Requests.Last(r => r.Method == HttpMethod.Post);
        Assert.Equal("/api/projects/proj_abc/issues/42/workflow-profile/prompts/plan_prompt/preview", postReq2.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task ConfigPreview_PromptKeyIsUrlEscaped()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup(req =>
        {
            if (req.Method == HttpMethod.Post)
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new { rendered = "ok", missing = new string[] { }, depth = 0 },
                });
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "workflow", "config", "preview", "42", "plan prompt"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var postReq = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        Assert.Equal("/api/projects/proj_abc/issues/42/workflow-profile/prompts/plan%20prompt/preview", postReq.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task ConfigPreview_PromptKeyContainingSlash_IsRejectedBeforeRequest()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "workflow", "config", "preview", "42", "bad/key"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("must not contain '/'", error.ToString());
    }

    [Fact]
    public async Task ConfigPreview_ServerError_SurfacesErrorAndExitsNonZero()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup(req =>
        {
            if (req.Method == HttpMethod.Post)
            {
                return RecordingHttpHandler.JsonError(
                    "Prompt 'plan_prompt' not found",
                    code: "prompt_not_found",
                    statusCode: HttpStatusCode.NotFound);
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "workflow", "config", "preview", "42", "plan_prompt"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Prompt 'plan_prompt' not found", error.ToString());
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
    public async Task ConfigSet_PromptInline_PutsBody()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup(req =>
        {
            if (req.Method == HttpMethod.Put && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42/workflow-profile/prompts/greeting")
            {
                return RecordingHttpHandler.Json(new { success = true, data = new { key = "greeting", body = "You are..." } });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "workflow", "config", "set", "42", "--prompt", "greeting=You are..."], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var putReq = handler.Requests.Single(r => r.Method == HttpMethod.Put);
        Assert.Equal("/api/projects/proj_abc/issues/42/workflow-profile/prompts/greeting", putReq.RequestUri?.PathAndQuery);
        var body = JsonNode.Parse(putReq.Body!) as JsonObject;
        Assert.Equal("You are...", body!["body"]?.GetValue<string>());
    }

    [Fact]
    public async Task ConfigSet_PromptInline_TableMode_RendersPromptResponse()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup(req =>
        {
            if (req.Method == HttpMethod.Put && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42/workflow-profile/prompts/greeting")
                return RecordingHttpHandler.Json(new { success = true, data = new { key = "greeting", body = "You are..." } });
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "workflow", "config", "set", "42", "--prompt", "greeting=You are...", "-o", "table"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("prompt: greeting", stdout);
        Assert.Contains("You are...", stdout);
    }

    [Fact]
    public async Task ConfigSet_PromptInline_JsonMode_EmitsPromptResponseJson()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup(req =>
        {
            if (req.Method == HttpMethod.Put && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42/workflow-profile/prompts/greeting")
                return RecordingHttpHandler.Json(new { success = true, data = new { key = "greeting", body = "You are..." } });
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "workflow", "config", "set", "42", "--prompt", "greeting=You are...", "-o", "json"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("\"key\":", stdout);
        Assert.Contains("\"greeting\"", stdout);
        Assert.Contains("\"body\":", stdout);
    }

    [Fact]
    public async Task ConfigSet_PromptAtFile_ReadsFileAndPutsBody()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup(req =>
        {
            if (req.Method == HttpMethod.Put && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42/workflow-profile/prompts/greeting")
            {
                return RecordingHttpHandler.Json(new { success = true, data = new { key = "greeting", body = "Hello from file." } });
            }
            return null!;
        });
        fs.AddFile("/prompts/greeting.md", "Hello from file.");

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "workflow", "config", "set", "42", "--prompt", "greeting=@/prompts/greeting.md"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var putReq = handler.Requests.Single(r => r.Method == HttpMethod.Put);
        var body = JsonNode.Parse(putReq.Body!) as JsonObject;
        Assert.Equal("Hello from file.", body!["body"]?.GetValue<string>());
    }

    [Fact]
    public async Task ConfigSet_PromptKey_IsUrlEscaped()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup(req =>
        {
            if (req.Method == HttpMethod.Put)
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "workflow", "config", "set", "42", "--prompt", "plan prompt=hi"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var putReq = handler.Requests.Single(r => r.Method == HttpMethod.Put);
        Assert.Equal("/api/projects/proj_abc/issues/42/workflow-profile/prompts/plan%20prompt", putReq.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task ConfigSet_PromptKeyContainingSlash_IsRejectedBeforeRequest()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "workflow", "config", "set", "42", "--prompt", "bad/key=hi"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("must not contain '/'", error.ToString());
    }

    [Fact]
    public async Task ConfigSet_VarWithInvalidPromptKey_MakesNoRequest()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["issue", "workflow", "config", "set", "42", "--var", "foo=bar", "--prompt", "bad/key=hi"],
            output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("must not contain '/'", error.ToString());
    }

    [Fact]
    public async Task ConfigSet_TemplateAndVarWithMissingPromptFile_MakesNoRequest()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup();
        fs.AddFile("/wf.yaml", "name: workflow\n");

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["issue", "workflow", "config", "set", "42", "--template", "@/wf.yaml", "--var", "foo=bar", "--prompt", "greeting=@/missing.md"],
            output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("could not read file", error.ToString());
    }

    [Fact]
    public async Task ConfigSet_Composite_IssuesAllThreeRequests()
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
                "--var", "foo=bar",
                "--prompt", "greeting=hi"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var puts = handler.Requests.Where(r => r.Method == HttpMethod.Put).ToList();
        var patches = handler.Requests.Where(r => r.Method == HttpMethod.Patch).ToList();
        Assert.Equal(2, puts.Count);
        Assert.Single(patches);
        Assert.Contains(puts, p => p.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42/workflow-profile/template");
        Assert.Contains(puts, p => p.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42/workflow-profile/prompts/greeting");
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
    public async Task ConfigSet_MalformedPromptNoEquals_PrintsErrorAndExitsNonZero()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "workflow", "config", "set", "42", "--prompt", "novalue"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("key=body", error.ToString());
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
    public async Task ConfigClear_Prompt_DeletesPrompt()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup(req =>
        {
            if (req.Method == HttpMethod.Delete && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42/workflow-profile/prompts/greeting")
            {
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "workflow", "config", "clear", "42", "--prompt", "greeting"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var deleteReq = handler.Requests.Single(r => r.Method == HttpMethod.Delete);
        Assert.Equal("/api/projects/proj_abc/issues/42/workflow-profile/prompts/greeting", deleteReq.RequestUri?.PathAndQuery);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Patch);
    }

    [Fact]
    public async Task ConfigClear_Prompt_TableMode_RendersDeletedPrompt()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup(req =>
        {
            if (req.Method == HttpMethod.Delete && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42/workflow-profile/prompts/greeting")
                return RecordingHttpHandler.Json(new { success = true });
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "workflow", "config", "clear", "42", "--prompt", "greeting", "-o", "table"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("prompt: greeting", stdout);
        Assert.Contains("deleted: yes", stdout);
    }

    [Fact]
    public async Task ConfigClear_Prompt_JsonMode_EmitsDeletedPromptJson()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup(req =>
        {
            if (req.Method == HttpMethod.Delete && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42/workflow-profile/prompts/greeting")
                return RecordingHttpHandler.Json(new { success = true });
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "workflow", "config", "clear", "42", "--prompt", "greeting", "-o", "json"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("\"key\":", stdout);
        Assert.Contains("\"greeting\"", stdout);
        Assert.Contains("\"deleted\": true", stdout);
        Assert.DoesNotContain("OK", stdout);
    }

    [Fact]
    public async Task ConfigClear_PromptKey_IsUrlEscaped()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup(req =>
        {
            if (req.Method == HttpMethod.Delete)
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "workflow", "config", "clear", "42", "--prompt", "plan prompt"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var deleteReq = handler.Requests.Single(r => r.Method == HttpMethod.Delete);
        Assert.Equal("/api/projects/proj_abc/issues/42/workflow-profile/prompts/plan%20prompt", deleteReq.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task ConfigClear_PromptKeyContainingSlash_IsRejectedBeforeRequest()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "workflow", "config", "clear", "42", "--prompt", "bad/key"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("must not contain '/'", error.ToString());
    }

    [Fact]
    public async Task ConfigClear_VarWithInvalidPromptKey_MakesNoRequest()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["issue", "workflow", "config", "clear", "42", "--var", "foo", "--prompt", "bad/key"],
            output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("must not contain '/'", error.ToString());
    }

    [Fact]
    public async Task ConfigClear_TemplateWithInvalidPromptKey_MakesNoRequest()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["issue", "workflow", "config", "clear", "42", "--template", "--prompt", "bad/key"],
            output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("must not contain '/'", error.ToString());
    }

    [Fact]
    public async Task ConfigClear_VarAndPrompt_IssuesBothAndLeavesOthersIntact()
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
                "--prompt", "greeting"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var patchReq = handler.Requests.Single(r => r.Method == HttpMethod.Patch);
        Assert.Equal("/api/projects/proj_abc/issues/42/workflow-profile/variables", patchReq.RequestUri?.PathAndQuery);
        var patchBody = JsonNode.Parse(patchReq.Body!) as JsonObject;
        AssertVariableSetToJsonNull(patchBody, "vars", "foo");

        var deleteReq = handler.Requests.Single(r => r.Method == HttpMethod.Delete);
        Assert.Equal("/api/projects/proj_abc/issues/42/workflow-profile/prompts/greeting", deleteReq.RequestUri?.PathAndQuery);

        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Put);
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
    public async Task ConfigClear_EmptyPromptKey_PrintsErrorAndExitsNonZero()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "workflow", "config", "clear", "42", "--prompt", ""], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("must not be empty", error.ToString());
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
