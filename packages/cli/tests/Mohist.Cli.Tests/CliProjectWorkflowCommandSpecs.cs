using System.Net;
using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public class CliProjectWorkflowCommandSpecs
{
    private static object SampleTemplateInfo() => new
    {
        projectId = "proj_abc",
        templateId = "tpl_abc",
        createdAt = "2026-06-29T10:00:00Z",
        updatedAt = "2026-06-29T10:15:00Z",
    };

    private static object SampleTemplateDefinitionPayload() => new
    {
        projectId = "proj_abc",
        templateId = "tpl_abc",
        definition = new
        {
            name = "workflow",
            tasks = new[]
            {
                new
                {
                    stage = "plan",
                    with = new { greeting = "hi" },
                },
            },
        },
    };

    private static HttpResponseMessage EmptyResponse(HttpStatusCode status, string reason)
    {
        return new HttpResponseMessage(status) { ReasonPhrase = reason };
    }

    [Fact]
    public async Task WorkflowHelp_ListsProfileTemplateAndConfigSubgroups()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();
        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "workflow", "--help"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("profile", stdout);
        Assert.Contains("template", stdout);
        Assert.Contains("config", stdout);
    }

    [Fact]
    public async Task TemplateHelp_ListsFiveVerbs()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();
        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "workflow", "template", "--help"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("list", stdout);
        Assert.Contains("create", stdout);
        Assert.Contains("show", stdout);
        Assert.Contains("update", stdout);
        Assert.Contains("delete", stdout);
    }

    [Fact]
    public async Task TemplateList_TableMode_SendsGetAndRendersTable()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/workflow-templates")
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new[]
                    {
                        new { projectId = "proj_abc", templateId = "tpl_abc", createdAt = "2026-06-29T10:00:00Z", updatedAt = "2026-06-29T10:15:00Z" },
                        new { projectId = "proj_abc", templateId = "tpl_xyz", createdAt = "2026-06-29T11:00:00Z", updatedAt = "2026-06-29T11:15:00Z" },
                    },
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "workflow", "template", "list", "-o", "table"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var getReq = handler.Requests.Single(r => r.Method == HttpMethod.Get);
        Assert.Equal("/api/projects/proj_abc/workflow-templates", getReq.RequestUri?.PathAndQuery);
        var stdout = output.ToString();
        Assert.Contains("tpl_abc", stdout);
        Assert.Contains("proj_abc", stdout);
        Assert.Contains("2026-06-29T10:00:00Z", stdout);
        Assert.Contains("tpl_xyz", stdout);
        Assert.DoesNotContain("default", stdout);
    }

    [Fact]
    public async Task TemplateList_JsonMode_EmitsRawPayload()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new[] { new { projectId = "proj_abc", templateId = "tpl_abc" } },
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "workflow", "template", "list", "-o", "json"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.DoesNotContain("\"success\"", stdout);
        Assert.Contains("tpl_abc", stdout);
    }

    [Fact]
    public async Task TemplateCreate_InlineYaml_SendsPostWithYamlBody()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/workflow-templates")
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = SampleTemplateInfo(),
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "workflow", "template", "create", "--yaml", "name: test"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var postReq = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        Assert.Equal("/api/projects/proj_abc/workflow-templates", postReq.RequestUri?.PathAndQuery);
        var body = JsonNode.Parse(postReq.Body!) as JsonObject;
        Assert.NotNull(body);
        Assert.Equal("name: test", body!["yaml"]?.GetValue<string>());
    }

    [Fact]
    public async Task TemplateCreate_FromFile_ReadsFileAndSendsContent()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/workflow-templates")
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = SampleTemplateInfo(),
                });
            }
            return null!;
        });
        fs.AddFile("/wf.yaml", "name: file-based\n");

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "workflow", "template", "create", "--yaml", "@/wf.yaml"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var postReq = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        var body = JsonNode.Parse(postReq.Body!) as JsonObject;
        Assert.Equal("name: file-based\n", body!["yaml"]?.GetValue<string>());
    }

    [Fact]
    public async Task TemplateCreate_TableMode_RendersCreatedTemplate()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post)
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = SampleTemplateInfo(),
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "workflow", "template", "create", "--yaml", "name: test", "-o", "table"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("tpl_abc", stdout);
        Assert.Contains("proj_abc", stdout);
        Assert.Contains("2026-06-29T10:15:00Z", stdout);
    }

    [Fact]
    public async Task TemplateCreate_JsonMode_EmitsRawPayload()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post)
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = SampleTemplateInfo(),
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "workflow", "template", "create", "--yaml", "name: test", "-o", "json"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.DoesNotContain("\"success\"", stdout);
        Assert.Contains("tpl_abc", stdout);
    }

    [Fact]
    public async Task TemplateCreate_ServerError_SurfacesErrorAndExitsNonZero()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post)
            {
                return RecordingHttpHandler.JsonError(
                    "Invalid YAML syntax",
                    code: "yaml_syntax",
                    statusCode: HttpStatusCode.BadRequest);
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "workflow", "template", "create", "--yaml", "bad: yaml:"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Invalid YAML syntax", error.ToString());
    }

    [Fact]
    public async Task TemplateShow_SendsGetWithTemplateIdAndRenders()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/workflow-templates/tpl_abc")
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = SampleTemplateDefinitionPayload(),
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "workflow", "template", "show", "tpl_abc", "-o", "table"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var getReq = handler.Requests.Single(r => r.Method == HttpMethod.Get);
        Assert.Equal("/api/projects/proj_abc/workflow-templates/tpl_abc", getReq.RequestUri?.PathAndQuery);
        var stdout = output.ToString();
        Assert.Contains("tpl_abc", stdout);
        Assert.Contains("definition:", stdout);
        Assert.Contains("\"stage\": \"plan\"", stdout);
        Assert.Contains("\"greeting\": \"hi\"", stdout);
    }

    [Fact]
    public async Task TemplateShow_NonexistentTemplate_Returns404()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return RecordingHttpHandler.JsonError(
                    "Template not found",
                    code: "template_not_found",
                    statusCode: HttpStatusCode.NotFound);
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "workflow", "template", "show", "nope"], output, error, fs, executor);

        Assert.Equal(4, exitCode);
        Assert.Contains("Template not found", error.ToString());
    }

    [Fact]
    public async Task TemplateUpdate_InlineYaml_SendsPutWithYamlBody()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Put && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/workflow-templates/tpl_abc")
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = SampleTemplateInfo(),
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "workflow", "template", "update", "tpl_abc", "--yaml", "name: updated"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var putReq = handler.Requests.Single(r => r.Method == HttpMethod.Put);
        Assert.Equal("/api/projects/proj_abc/workflow-templates/tpl_abc", putReq.RequestUri?.PathAndQuery);
        var body = JsonNode.Parse(putReq.Body!) as JsonObject;
        Assert.Equal("name: updated", body!["yaml"]?.GetValue<string>());
    }

    [Fact]
    public async Task TemplateUpdate_FromFile_ReadsFileAndSendsContent()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Put && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/workflow-templates/tpl_abc")
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = SampleTemplateInfo(),
                });
            }
            return null!;
        });
        fs.AddFile("/update.yaml", "name: updated-from-file\n");

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "workflow", "template", "update", "tpl_abc", "--yaml", "@/update.yaml"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var putReq = handler.Requests.Single(r => r.Method == HttpMethod.Put);
        var body = JsonNode.Parse(putReq.Body!) as JsonObject;
        Assert.Equal("name: updated-from-file\n", body!["yaml"]?.GetValue<string>());
    }

    [Fact]
    public async Task TemplateUpdate_NonexistentTemplate_Returns404()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Put)
            {
                return RecordingHttpHandler.JsonError(
                    "Template not found",
                    code: "template_not_found",
                    statusCode: HttpStatusCode.NotFound);
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "workflow", "template", "update", "nope", "--yaml", "name: x"], output, error, fs, executor);

        Assert.Equal(4, exitCode);
        Assert.Contains("Template not found", error.ToString());
    }

    [Fact]
    public async Task TemplateDelete_SendsDeleteAndReturnsSuccess()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Delete && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/workflow-templates/tpl_abc")
            {
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "workflow", "template", "delete", "tpl_abc"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var deleteReq = handler.Requests.Single(r => r.Method == HttpMethod.Delete);
        Assert.Equal("/api/projects/proj_abc/workflow-templates/tpl_abc", deleteReq.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task TemplateDelete_NonexistentTemplate_Returns404()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Delete)
            {
                return RecordingHttpHandler.JsonError(
                    "Template not found",
                    code: "template_not_found",
                    statusCode: HttpStatusCode.NotFound);
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "workflow", "template", "delete", "nope"], output, error, fs, executor);

        Assert.Equal(4, exitCode);
        Assert.Contains("Template not found", error.ToString());
    }

    [Fact]
    public async Task TemplateList_AcceptsProjectAndProjectIdAlias()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get)
                return RecordingHttpHandler.Json(new { success = true, data = new[] { new { projectId = "proj_abc", templateId = "tpl_a" } } });
            return null!;
        });

        var byName = await MohistCliCommands.RunAsync(
            http, ["project", "workflow", "template", "list", "--project", "proj_abc", "-o", "json"], output, error, fs, executor);
        Assert.Equal(0, byName);
        Assert.Contains(handler.Requests.Where(r => r.Method == HttpMethod.Get),
            r => r.RequestUri?.PathAndQuery == "/api/projects/proj_abc/workflow-templates");

        var byId = await MohistCliCommands.RunAsync(
            http, ["project", "workflow", "template", "list", "--project", "proj_abc", "-o", "json"], output, error, fs, executor);
        Assert.Equal(0, byId);
        Assert.Contains(handler.Requests.Where(r => r.Method == HttpMethod.Get),
            r => r.RequestUri?.PathAndQuery == "/api/projects/proj_abc/workflow-templates");
    }

    [Fact]
    public async Task TemplateCreate_InvalidOutputMode_PrintsErrorAndExitsOne()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "workflow", "template", "create", "--yaml", "x", "-o", "yaml"], output, error, fs, executor);

        Assert.Equal(1, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("table", error.ToString());
        Assert.Contains("json", error.ToString());
    }

    // ──────────────────────────────────────────────
    // Config command group
    // ──────────────────────────────────────────────

    [Fact]
    public async Task ConfigHelp_ListsFourVerbs()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();
        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "workflow", "config", "--help"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("get", stdout);
        Assert.Contains("set", stdout);
        Assert.Contains("clear", stdout);
        Assert.Contains("preview", stdout);
    }

    [Fact]
    public async Task ConfigGet_TableMode_SendsGetAndRendersDefaultTemplateVariablesPrompts()
    {
        var getCount = 0;
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/workflow-profile")
            {
                getCount++;
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new
                    {
                        defaultTemplateId = "tpl_abc",
                        profileId = "mohist/github-pr",
                        variables = new
                        {
                            vars = new Dictionary<string, object> { ["foo"] = "bar" },
                            stages = new Dictionary<string, object>(),
                        },
                    },
                });
            }
            if (req.Method == HttpMethod.Get && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/workflow-profile/prompts")
            {
                getCount++;
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new[]
                    {
                        new
                        {
                            key = "plan_prompt",
                            displayName = "Plan Prompt",
                            description = "Plan instructions",
                            tags = Array.Empty<string>(),
                            stage = "plan",
                            body = "You are a planner",
                            source = "project",
                        },
                        new
                        {
                            key = "check_prompt",
                            displayName = "Check Prompt",
                            description = "Check instructions",
                            tags = Array.Empty<string>(),
                            stage = "check",
                            body = "You are a reviewer",
                            source = "system",
                        },
                    },
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "workflow", "config", "get", "-o", "table"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Equal(2, getCount);
        var stdout = output.ToString();
        Assert.Contains("tpl_abc", stdout);
        Assert.Contains("foo", stdout);
        Assert.Contains("bar", stdout);
        Assert.Contains("plan_prompt", stdout);
        Assert.Contains("source: project", stdout);
        Assert.Contains("You are a planner", stdout);
    }

    [Fact]
    public async Task ConfigGet_JsonMode_EmitsRawPayload()
    {
        var getCount = 0;
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/workflow-profile")
            {
                getCount++;
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new
                    {
                        defaultTemplateId = "tpl_abc",
                        variables = new { vars = new { foo = "bar" }, stages = new { } },
                    },
                });
            }
            if (req.Method == HttpMethod.Get && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/workflow-profile/prompts")
            {
                getCount++;
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new[]
                    {
                        new { key = "plan_prompt", body = "You are a planner", source = "project" },
                    },
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "workflow", "config", "get", "-o", "json"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.DoesNotContain("\"success\"", stdout);
        Assert.Contains("tpl_abc", stdout);
        Assert.Contains("foo", stdout);
        Assert.Contains("plan_prompt", stdout);
        Assert.Contains("You are a planner", stdout);
        Assert.Equal(2, getCount);
    }

    [Fact]
    public async Task ConfigGet_EmptySuccessBodyOnProfile_PrintsReasonAndExitsOne()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/workflow-profile")
                return EmptyResponse(HttpStatusCode.NoContent, "No Content");

            return RecordingHttpHandler.Json(new { success = true, data = Array.Empty<object>() });
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "workflow", "config", "get", "-o", "table"], output, error, fs, executor);

        Assert.Equal(1, exitCode);
        Assert.Contains("No Content", error.ToString());
        Assert.Equal(string.Empty, output.ToString());
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ConfigSet_DefaultTemplate_SendsPut()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Put && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/workflow-profile/default-template")
            {
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "workflow", "config", "set", "--default-template", "tpl_abc"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var putReq = handler.Requests.Single(r => r.Method == HttpMethod.Put);
        Assert.Equal("/api/projects/proj_abc/workflow-profile/default-template", putReq.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task ConfigSet_VarAndStageVar_SendsPatchOnly()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Patch && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/workflow-profile/variables")
            {
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "workflow", "config", "set", "--var", "foo=bar", "--stage-var", "plan.baz=qux"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Single(handler.Requests);
        var patchReq = handler.Requests.Single(r => r.Method == HttpMethod.Patch);
        Assert.Equal("/api/projects/proj_abc/workflow-profile/variables", patchReq.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task ConfigSet_VarAndStageVar_PatchBodyHasCorrectShape()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Patch && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/workflow-profile/variables")
            {
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "workflow", "config", "set", "--var", "foo=bar", "--stage-var", "plan.baz=qux"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var patchReq = handler.Requests.Single(r => r.Method == HttpMethod.Patch);
        var body = JsonNode.Parse(patchReq.Body!) as JsonObject;
        Assert.NotNull(body);
        Assert.Equal("bar", body!["vars"]?["foo"]?.GetValue<string>());
        Assert.Equal("qux", body["stages"]?["plan"]?["vars"]?["baz"]?.GetValue<string>());
    }

    [Fact]
    public async Task ConfigSet_VarsFile_SendsPutVariables()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Put && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/workflow-profile/variables")
            {
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            }
            return null!;
        });
        fs.AddFile("/vars.json", "{\"vars\": {\"foo\": \"bar\"}}");

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "workflow", "config", "set", "--vars-file", "/vars.json"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Single(handler.Requests);
        var putReq = handler.Requests.Single(r => r.Method == HttpMethod.Put);
        Assert.Equal("/api/projects/proj_abc/workflow-profile/variables", putReq.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task ConfigSet_VarsFileAndVar_MutuallyExclusive()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "workflow", "config", "set", "--vars-file", "/vars.json", "--var", "foo=bar"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("mutually exclusive", error.ToString());
    }

    [Fact]
    public async Task ConfigSet_VarsFileAndStageVar_MutuallyExclusive()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "workflow", "config", "set", "--vars-file", "/vars.json", "--stage-var", "plan.baz=qux"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("mutually exclusive", error.ToString());
    }

    [Fact]
    public async Task ConfigSet_NoFlags_ExitsNonZero()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "workflow", "config", "set"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("nothing to change", error.ToString());
    }

    [Fact]
    public async Task ConfigSet_PromptInline_SendsPut()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Put && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/workflow-profile/prompts/greeting")
            {
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "workflow", "config", "set", "--prompt", "greeting=You are..."], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var putReq = handler.Requests.Single(r => r.Method == HttpMethod.Put);
        Assert.Equal("/api/projects/proj_abc/workflow-profile/prompts/greeting", putReq.RequestUri?.PathAndQuery);
        var body = JsonNode.Parse(putReq.Body!) as JsonObject;
        Assert.Equal("You are...", body!["body"]?.GetValue<string>());
    }

    [Fact]
    public async Task ConfigSet_PromptFromFile_SendsPutWithFileContent()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Put && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/workflow-profile/prompts/greeting")
            {
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            }
            return null!;
        });
        fs.AddFile("/prompt.md", "You are a helpful assistant");

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "workflow", "config", "set", "--prompt", "greeting=@/prompt.md"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var putReq = handler.Requests.Single(r => r.Method == HttpMethod.Put);
        var body = JsonNode.Parse(putReq.Body!) as JsonObject;
        Assert.Equal("You are a helpful assistant", body!["body"]?.GetValue<string>());
    }

    [Fact]
    public async Task ConfigSet_Composite_SendsMultipleRequests()
    {
        var callCount = 0;
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            callCount++;
            if (req.Method == HttpMethod.Put && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/workflow-profile/default-template")
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            if (req.Method == HttpMethod.Patch && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/workflow-profile/variables")
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            if (req.Method == HttpMethod.Put && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/workflow-profile/prompts/greeting")
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "workflow", "config", "set", "--default-template", "tpl_abc", "--var", "foo=bar", "--prompt", "greeting=Hi"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Equal(3, callCount);
    }

    [Fact]
    public async Task ConfigClear_DefaultTemplate_SendsDelete()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Delete && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/workflow-profile/default-template")
            {
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "workflow", "config", "clear", "--default-template"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Single(handler.Requests);
        var deleteReq = handler.Requests.Single(r => r.Method == HttpMethod.Delete);
        Assert.Equal("/api/projects/proj_abc/workflow-profile/default-template", deleteReq.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task ConfigClear_Var_SendsPatchWithNull()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Patch && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/workflow-profile/variables")
            {
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "workflow", "config", "clear", "--var", "foo"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var patchReq = handler.Requests.Single(r => r.Method == HttpMethod.Patch);
        var body = JsonNode.Parse(patchReq.Body!) as JsonObject;
        Assert.NotNull(body);
        Assert.True(body!["vars"]?["foo"] is null);
    }

    [Fact]
    public async Task ConfigClear_Prompt_SendsDelete()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Delete && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/workflow-profile/prompts/greeting")
            {
                return RecordingHttpHandler.Json(new { success = true, data = new { key = "greeting", deleted = true } });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "workflow", "config", "clear", "--prompt", "greeting"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var deleteReq = handler.Requests.Single(r => r.Method == HttpMethod.Delete);
        Assert.Equal("/api/projects/proj_abc/workflow-profile/prompts/greeting", deleteReq.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task ConfigClear_NoFlags_ExitsNonZero()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "workflow", "config", "clear"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("nothing to clear", error.ToString());
    }

    [Fact]
    public async Task ConfigClear_Composite_SendsMultipleRequests()
    {
        var callCount = 0;
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            callCount++;
            if (req.Method == HttpMethod.Delete && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/workflow-profile/default-template")
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            if (req.Method == HttpMethod.Patch && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/workflow-profile/variables")
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            if (req.Method == HttpMethod.Delete && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/workflow-profile/prompts/greeting")
                return RecordingHttpHandler.Json(new { success = true, data = new { key = "greeting", deleted = true } });
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "workflow", "config", "clear", "--default-template", "--var", "foo", "--prompt", "greeting"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Equal(3, callCount);
    }

    [Fact]
    public async Task ConfigPreview_SendsPostAndPrintsRendered()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/workflow-profile/prompts/plan_prompt/preview")
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new { rendered = "You are a planner. Focus on architecture." },
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "workflow", "config", "preview", "plan_prompt"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var postReq = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        Assert.Equal("/api/projects/proj_abc/workflow-profile/prompts/plan_prompt/preview", postReq.RequestUri?.PathAndQuery);
        var stdout = output.ToString();
        Assert.Contains("architecture", stdout);
    }

    [Fact]
    public async Task ConfigPreview_EmptyKey_ExitsNonZero()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "workflow", "config", "preview", ""], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("prompt key is required", error.ToString());
    }

    [Fact]
    public async Task ConfigGet_NoActiveProject_PrintsError()
    {
        var handler = new RecordingHttpHandler((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = new { } })));
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:3456") };
        var output = new StringWriter();
        var error = new StringWriter();
        var fs = new FakeFileSystem();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "workflow", "config", "get"], output, error, fs, new FakeCommandExecutor());

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("mo project use", error.ToString());
    }

    [Fact]
    public async Task TemplateList_NoActiveProject_PrintsError()
    {
        var handler = new RecordingHttpHandler((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = new { } })));
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:3456") };
        var output = new StringWriter();
        var error = new StringWriter();
        var fs = new FakeFileSystem();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "workflow", "template", "list"], output, error, fs, new FakeCommandExecutor());

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("mo project use", error.ToString());
    }
}
