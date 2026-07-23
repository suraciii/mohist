using System.Net;
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

    [Theory]
    [InlineData("set")]
    [InlineData("clear")]
    public async Task ConfigMutationHelp_AdvertisesOnlyTemplateChanges(string verb)
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup();
        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "workflow", "config", verb, "--help"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("--template", stdout);
        Assert.DoesNotContain("variable", stdout.ToLowerInvariant());
        Assert.Empty(handler.Requests);
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
            http, ["issue", "workflow", "config", "get", "42",], output, error, fs, executor);

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
            http, ["issue", "workflow", "config", "get", "42", "--json", "issueNumber,profileId"], output, error, fs, executor);

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
            http, ["issue", "workflow", "config", "get", "42", "--project", "proj_abc"], output, error, fs, executor);
        Assert.Equal(0, byId);
        Assert.Contains(handler.Requests.Where(r => r.Method == HttpMethod.Get),
            r => r.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42/workflow-profile");
    }

    [Fact]
    public async Task ConfigGet_LegacyOutputOption_IsRejectedLocally()
    {
        var (handler, http, output, error, fs, executor) = CreateWorkflowConfigSetup();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "workflow", "config", "get", "42", "--output", "json"], output, error, fs, executor);

        Assert.Equal(2, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("--output", error.ToString());
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
            http, ["issue", "workflow", "config", "get", "42",], output, error, fs, executor);

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
