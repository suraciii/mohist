using System.Net;
using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public class CliIssueTemplateCommandSpecs
{
    private static readonly object FeatureDetail = new
    {
        id = "feature",
        name = "Feature",
        description = "Standard three-voice PRD with acceptance criteria and non-goals.",
        source = "builtin",
        body = string.Join("\n", new[]
        {
            "## User Voice",
            "<!-- Describe the user need. -->",
            "<user need>",
            "",
            "## Product Shape",
            "<!-- Describe the product boundary. -->",
            "<product shape>",
            "",
            "## Domain Model",
            "<!-- Describe the domain model. -->",
            "<domain model>",
            "",
            "## Acceptance Criteria",
            "<!-- List observable outcomes. -->",
            "- [ ] criterion",
            "",
            "## Non-Goals",
            "<!-- List explicitly out-of-scope items. -->",
            "- non-goal",
        }),
    };

    private static (RecordingHttpHandler Handler, HttpClient Http, StringWriter Output, StringWriter Error, FakeFileSystem Fs, FakeCommandExecutor Executor)
        CreateIssueTemplateSetup(string? activeProjectId = "proj_abc", Func<HttpRequestMessage, HttpResponseMessage>? responder = null)
    {
        var defaultResponder = new Func<HttpRequestMessage, HttpResponseMessage>(req =>
        {
            var path = req.RequestUri?.AbsolutePath ?? "";
            if (path.EndsWith("/issue-templates", StringComparison.Ordinal) && req.Method == HttpMethod.Get)
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new[]
                    {
                        new
                        {
                            id = "feature",
                            name = "Feature",
                            description = "Standard three-voice PRD with acceptance criteria and non-goals.",
                            source = "builtin",
                        },
                        new
                        {
                            id = "team/bugfix",
                            name = "Team Bugfix",
                            description = "Lightweight bugfix template for the support team.",
                            source = "custom",
                        },
                    },
                });
            }
            if (path.EndsWith("/issue-templates/feature", StringComparison.Ordinal) && req.Method == HttpMethod.Get)
            {
                return RecordingHttpHandler.Json(new { success = true, data = FeatureDetail });
            }
            if (path.EndsWith("/issue-templates/mohist/default", StringComparison.Ordinal) && req.Method == HttpMethod.Get)
            {
                return RecordingHttpHandler.Json(new { success = true, data = FeatureDetail });
            }
            if (path.Contains("/issue-templates/", StringComparison.Ordinal) && req.Method == HttpMethod.Get)
            {
                var segments = path.Split('/');
                var id = segments[^1].Split('?')[0];
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new
                    {
                        id,
                        name = id,
                        description = $"Description for {id}",
                        source = "custom",
                        body = "## Section 1\n<!-- Guidance for section 1 -->\n<placeholder 1>",
                    },
                });
            }
            return RecordingHttpHandler.Json(new { success = true, data = new { } });
        });
        return CliTestFactory.Create(
            (req, _) => Task.FromResult(responder is null ? defaultResponder(req) : responder(req)),
            activeProjectId);
    }

    [Fact]
    public async Task TemplateList_HitsIssueTemplatesListEndpoint()
    {
        var (handler, http, output, error, fs, executor) = CreateIssueTemplateSetup();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "template", "list"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var req = handler.Requests.Last();
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Equal("/api/issue-templates?projectId=proj_abc", req.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task TemplateList_WithLsAlias_HitsSameEndpoint()
    {
        var (handler, http, output, error, fs, executor) = CreateIssueTemplateSetup();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "template", "ls"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var req = handler.Requests.Last();
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Equal("/api/issue-templates?projectId=proj_abc", req.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task TemplateList_NoActiveProject_ExitsWithError()
    {
        var (handler, http, output, error, fs, executor) = CreateIssueTemplateSetup(activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "template", "list"], output, error, fs, executor);

        Assert.Equal(1, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("mo project use", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TemplateList_WithExplicitProject_ResolvesProject()
    {
        var (handler, http, output, error, fs, executor) = CreateIssueTemplateSetup();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "template", "list", "--project", "proj_xyz"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var req = handler.Requests.Last();
        Assert.Equal("/api/issue-templates?projectId=proj_xyz", req.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task TemplateList_Table_RendersNameAndDescription()
    {
        var (handler, http, output, error, fs, executor) = CreateIssueTemplateSetup();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "template", "list",], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("name", text);
        Assert.Contains("description", text);
        Assert.Contains("Feature", text);
        Assert.Contains("Team Bugfix", text);
        Assert.Contains("builtin", text);
        Assert.Contains("custom", text);
        Assert.DoesNotContain("about", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("default", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TemplateList_Json_PassesThroughServerEnvelope()
    {
        var (handler, http, output, error, fs, executor) = CreateIssueTemplateSetup();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "template", "list"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var json = JsonNode.Parse(output.ToString()) as JsonArray;
        Assert.NotNull(json);
        Assert.Equal(2, json!.Count);
        var ids = json.Select(n => n?["id"]?.GetValue<string>()).ToArray();
        Assert.Contains("feature", ids);
        Assert.Contains("team/bugfix", ids);
    }

    [Fact]
    public async Task TemplateList_EmptyArray_RendersNoTemplatesMessage()
    {
        var handler = new RecordingHttpHandler(async (_, _) =>
        {
            return await Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new object[] { },
            }));
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:3456") };
        var output = new StringWriter();
        var error = new StringWriter();
        var fs = new FakeFileSystem();
        fs.AddFile(
            Path.Combine(CliTestFactory.UserHome, ".mohist", "cli-state.json"),
            "{\"activeProjectId\":\"proj_abc\"}");
        var executor = new FakeCommandExecutor();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "template", "list",], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Contains("No issue templates", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TemplateList_ServerError_ReturnsExitCodeOne()
    {
        var handler = new RecordingHttpHandler(async (_, _) =>
        {
            return await Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = false,
                error = "boom",
            }, HttpStatusCode.InternalServerError));
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:3456") };
        var output = new StringWriter();
        var error = new StringWriter();
        var fs = new FakeFileSystem();
        fs.AddFile(
            Path.Combine(CliTestFactory.UserHome, ".mohist", "cli-state.json"),
            "{\"activeProjectId\":\"proj_abc\"}");
        var executor = new FakeCommandExecutor();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "template", "list"], output, error, fs, executor);

        Assert.Equal(1, exitCode);
        Assert.Contains("boom", error.ToString());
    }

    [Fact]
    public async Task TemplateGet_FeatureTemplate_HitsFeaturePath()
    {
        var (handler, http, output, error, fs, executor) = CreateIssueTemplateSetup();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "template", "get", "feature"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var req = handler.Requests.Last();
        Assert.Equal(HttpMethod.Get, req.Method);
        var path = req.RequestUri?.PathAndQuery ?? "";
        Assert.Equal("/api/issue-templates/feature?projectId=proj_abc", path);
    }

    [Fact]
    public async Task TemplateGet_LegacyAlias_HitsMohistDefaultPath()
    {
        var (handler, http, output, error, fs, executor) = CreateIssueTemplateSetup();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "template", "get", "mohist/default"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var req = handler.Requests.Last();
        Assert.Equal(HttpMethod.Get, req.Method);
        var path = req.RequestUri?.PathAndQuery ?? "";
        Assert.Equal("/api/issue-templates/mohist/default?projectId=proj_abc", path);
    }

    [Fact]
    public async Task TemplateGet_CustomTemplate_HitsTemplatePath()
    {
        var handler = new RecordingHttpHandler(async (_, _) =>
        {
            return await Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    id = "team/bugfix",
                    name = "Team Bugfix",
                    description = "Bugfix template.",
                    source = "custom",
                    body = "## Bug Summary\n<!-- What broke and how to reproduce. -->\n<bug summary>",
                },
            }));
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:3456") };
        var output = new StringWriter();
        var error = new StringWriter();
        var fs = new FakeFileSystem();
        fs.AddFile(
            Path.Combine(CliTestFactory.UserHome, ".mohist", "cli-state.json"),
            "{\"activeProjectId\":\"proj_abc\"}");
        var executor = new FakeCommandExecutor();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "template", "get", "team/bugfix"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var req = handler.Requests.Last();
        Assert.Equal("/api/issue-templates/team/bugfix?projectId=proj_abc", req.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task TemplateGet_NoActiveProject_ExitsWithError()
    {
        var (handler, http, output, error, fs, executor) = CreateIssueTemplateSetup(activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "template", "get", "feature"], output, error, fs, executor);

        Assert.Equal(1, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("mo project use", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TemplateGet_Table_DisplaysMetadataAndBody()
    {
        var (handler, http, output, error, fs, executor) = CreateIssueTemplateSetup();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "template", "get", "feature",], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("id:", text);
        Assert.Contains("feature", text);
        Assert.Contains("description:", text);
        Assert.Contains("source:", text);
        Assert.Contains("builtin", text);
        Assert.Contains("body:", text);
        // Body is rendered verbatim — section headings, inline guidance comments, placeholders.
        Assert.Contains("## User Voice", text);
        Assert.Contains("<!-- Describe the user need. -->", text);
        Assert.Contains("<user need>", text);
        Assert.Contains("## Non-Goals", text);
        Assert.DoesNotContain("about:", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sections:", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TemplateGet_PrintsAllFiveSectionTitlesInOrder()
    {
        var (handler, http, output, error, fs, executor) = CreateIssueTemplateSetup();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "template", "get", "feature",], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        var userVoiceIdx = text.IndexOf("User Voice", StringComparison.Ordinal);
        var productShapeIdx = text.IndexOf("Product Shape", StringComparison.Ordinal);
        var domainModelIdx = text.IndexOf("Domain Model", StringComparison.Ordinal);
        var acceptanceCriteriaIdx = text.IndexOf("Acceptance Criteria", StringComparison.Ordinal);
        var nonGoalsIdx = text.IndexOf("Non-Goals", StringComparison.Ordinal);
        Assert.True(userVoiceIdx > 0);
        Assert.True(productShapeIdx > userVoiceIdx);
        Assert.True(domainModelIdx > productShapeIdx);
        Assert.True(acceptanceCriteriaIdx > domainModelIdx);
        Assert.True(nonGoalsIdx > acceptanceCriteriaIdx);
    }

    [Fact]
    public async Task TemplateGet_FeatureAndLegacyAlias_RenderIdentically()
    {
        var (handler, http, output, error, fs, executor) = CreateIssueTemplateSetup();

        var exitCode1 = await MohistCliCommands.RunAsync(
            http, ["issue", "template", "get", "feature",], output, error, fs, executor);
        Assert.Equal(0, exitCode1);
        var featureText = output.ToString();
        output.GetStringBuilder().Clear();

        var exitCode2 = await MohistCliCommands.RunAsync(
            http, ["issue", "template", "get", "mohist/default",], output, error, fs, executor);
        Assert.Equal(0, exitCode2);
        var aliasText = output.ToString();

        Assert.Equal(featureText, aliasText);
    }

    [Fact]
    public async Task TemplateGet_NotFound_ReturnsExitCodeFour()
    {
        var handler = new RecordingHttpHandler(async (_, _) =>
        {
            return await Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = false,
                error = "Issue template 'nonexistent' not found",
            }, HttpStatusCode.NotFound));
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:3456") };
        var output = new StringWriter();
        var error = new StringWriter();
        var fs = new FakeFileSystem();
        fs.AddFile(
            Path.Combine(CliTestFactory.UserHome, ".mohist", "cli-state.json"),
            "{\"activeProjectId\":\"proj_abc\"}");
        var executor = new FakeCommandExecutor();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "template", "get", "nonexistent"], output, error, fs, executor);

        Assert.Equal(1, exitCode);
        Assert.Contains("not found", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TemplateGet_Json_PassesThroughServerEnvelope()
    {
        var (handler, http, output, error, fs, executor) = CreateIssueTemplateSetup();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "template", "get", "feature"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var json = JsonNode.Parse(output.ToString()) as JsonObject;
        Assert.NotNull(json);
        Assert.Equal("feature", json!["id"]?.GetValue<string>());
        Assert.Equal("Feature", json["name"]?.GetValue<string>());
        var body = json["body"]?.GetValue<string>();
        Assert.False(string.IsNullOrEmpty(body));
        Assert.Contains("## User Voice", body);
        Assert.Contains("## Non-Goals", body);
    }

    [Fact]
    public async Task TemplateGet_ServerUnavailable_ReturnsExitCodeOne()
    {
        var handler = new RecordingHttpHandler(async (_, _) =>
        {
            throw new HttpRequestException("connection refused");
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:3456") };
        var output = new StringWriter();
        var error = new StringWriter();
        var fs = new FakeFileSystem();
        fs.AddFile(
            Path.Combine(CliTestFactory.UserHome, ".mohist", "cli-state.json"),
            "{\"activeProjectId\":\"proj_abc\"}");
        var executor = new FakeCommandExecutor();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "template", "get", "feature"], output, error, fs, executor);

        Assert.Equal(1, exitCode);
        Assert.Contains("mo server start", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TemplateList_InvalidOutputMode_ReturnsExitCodeOne()
    {
        var (handler, http, output, error, fs, executor) = CreateIssueTemplateSetup();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "template", "list", "-o", "yaml"], output, error, fs, executor);

        Assert.Equal(1, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("table", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("json", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}
