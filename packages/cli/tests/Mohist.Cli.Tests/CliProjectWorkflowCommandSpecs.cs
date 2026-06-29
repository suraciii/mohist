using System.Net;
using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public class CliProjectWorkflowCommandSpecs
{
    private static (RecordingHttpHandler Handler, HttpClient Http, StringWriter Output, StringWriter Error, FakeFileSystem Fs, FakeCommandExecutor Executor)
        CreateHarness(Func<HttpRequestMessage, HttpResponseMessage>? responder = null)
    {
        var handler = new RecordingHttpHandler((req, ct) =>
        {
            if (responder is not null) return Task.FromResult(responder(req));
            return Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = new { } }));
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:3456") };
        var output = new StringWriter();
        var error = new StringWriter();
        var fs = new FakeFileSystem();
        fs.AddFile(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mohist", "cli-state.json"),
            "{\"activeProjectId\":\"proj_abc\"}");
        return (handler, http, output, error, fs, new FakeCommandExecutor());
    }

    private static object SampleTemplate(bool includeYaml = true) => new
    {
        id = "tpl_abc",
        name = "default",
        description = "Default workflow template",
        isDefault = true,
        yaml = includeYaml ? "name: workflow\ntasks:\n  - stage: plan\n    with:\n      greeting: hi\n" : null,
    };

    [Fact]
    public async Task WorkflowHelp_ListsTemplateAndConfigSubgroups()
    {
        var (handler, http, output, error, fs, executor) = CreateHarness();
        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "workflow", "--help"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("template", stdout);
        Assert.Contains("config", stdout);
    }

    [Fact]
    public async Task TemplateHelp_ListsFiveVerbs()
    {
        var (handler, http, output, error, fs, executor) = CreateHarness();
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
        var (handler, http, output, error, fs, executor) = CreateHarness(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/workflow-templates")
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new[]
                    {
                        new { id = "tpl_abc", name = "default", description = "Default template", isDefault = true },
                        new { id = "tpl_xyz", name = "custom", description = "Custom template", isDefault = false },
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
        Assert.Contains("default", stdout);
        Assert.Contains("tpl_xyz", stdout);
        Assert.Contains("custom", stdout);
    }

    [Fact]
    public async Task TemplateList_JsonMode_EmitsRawPayload()
    {
        var (handler, http, output, error, fs, executor) = CreateHarness(req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new[] { new { id = "tpl_abc", name = "default" } },
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
        var (handler, http, output, error, fs, executor) = CreateHarness(req =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/workflow-templates")
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = SampleTemplate(),
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
        var (handler, http, output, error, fs, executor) = CreateHarness(req =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/workflow-templates")
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = SampleTemplate(),
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
        var (handler, http, output, error, fs, executor) = CreateHarness(req =>
        {
            if (req.Method == HttpMethod.Post)
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = SampleTemplate(),
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "workflow", "template", "create", "--yaml", "name: test", "-o", "table"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("tpl_abc", stdout);
        Assert.Contains("default", stdout);
    }

    [Fact]
    public async Task TemplateCreate_JsonMode_EmitsRawPayload()
    {
        var (handler, http, output, error, fs, executor) = CreateHarness(req =>
        {
            if (req.Method == HttpMethod.Post)
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = SampleTemplate(),
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
        var (handler, http, output, error, fs, executor) = CreateHarness(req =>
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
        var (handler, http, output, error, fs, executor) = CreateHarness(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/workflow-templates/tpl_abc")
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = SampleTemplate(),
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
        Assert.Contains("default", stdout);
    }

    [Fact]
    public async Task TemplateShow_NonexistentTemplate_Returns404()
    {
        var (handler, http, output, error, fs, executor) = CreateHarness(req =>
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
        var (handler, http, output, error, fs, executor) = CreateHarness(req =>
        {
            if (req.Method == HttpMethod.Put && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/workflow-templates/tpl_abc")
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = SampleTemplate(),
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
        var (handler, http, output, error, fs, executor) = CreateHarness(req =>
        {
            if (req.Method == HttpMethod.Put && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/workflow-templates/tpl_abc")
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = SampleTemplate(),
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
        var (handler, http, output, error, fs, executor) = CreateHarness(req =>
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
        var (handler, http, output, error, fs, executor) = CreateHarness(req =>
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
        var (handler, http, output, error, fs, executor) = CreateHarness(req =>
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
        var (handler, http, output, error, fs, executor) = CreateHarness(req =>
        {
            if (req.Method == HttpMethod.Get)
                return RecordingHttpHandler.Json(new { success = true, data = new[] { new { id = "tpl_a", name = "t" } } });
            return null!;
        });

        var byName = await MohistCliCommands.RunAsync(
            http, ["project", "workflow", "template", "list", "--project", "proj_abc", "-o", "json"], output, error, fs, executor);
        Assert.Equal(0, byName);
        Assert.Contains(handler.Requests.Where(r => r.Method == HttpMethod.Get),
            r => r.RequestUri?.PathAndQuery == "/api/projects/proj_abc/workflow-templates");

        var byId = await MohistCliCommands.RunAsync(
            http, ["project", "workflow", "template", "list", "--project-id", "proj_abc", "-o", "json"], output, error, fs, executor);
        Assert.Equal(0, byId);
        Assert.Contains(handler.Requests.Where(r => r.Method == HttpMethod.Get),
            r => r.RequestUri?.PathAndQuery == "/api/projects/proj_abc/workflow-templates");
    }

    [Fact]
    public async Task TemplateCreate_InvalidOutputMode_PrintsErrorAndExitsOne()
    {
        var (handler, http, output, error, fs, executor) = CreateHarness();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "workflow", "template", "create", "--yaml", "x", "-o", "yaml"], output, error, fs, executor);

        Assert.Equal(1, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("table", error.ToString());
        Assert.Contains("json", error.ToString());
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
