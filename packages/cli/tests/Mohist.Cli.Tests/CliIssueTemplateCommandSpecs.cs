using System.Net;
using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public class CliIssueTemplateCommandSpecs
{
    private static (RecordingHttpHandler Handler, HttpClient Http, StringWriter Output, StringWriter Error, FakeFileSystem Fs, FakeCommandExecutor Executor)
        CreateHarness(string? activeProjectId = "proj_abc", Func<HttpRequestMessage, HttpResponseMessage>? responder = null)
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
                            id = "mohist/default",
                            name = "Mohist Default",
                            about = "Standard three-voice PRD with acceptance criteria and non-goals.",
                            suitableFor = new[] { "feature development", "bug fixes" },
                            isDefault = true,
                            source = "builtin",
                        },
                        new
                        {
                            id = "team/bugfix",
                            name = "Team Bugfix",
                            about = "Lightweight bugfix template for the support team.",
                            suitableFor = new[] { "bug fixes" },
                            isDefault = false,
                            source = "custom",
                        },
                    },
                });
            }
            if (path.EndsWith("/issue-templates/mohist/default", StringComparison.Ordinal) && req.Method == HttpMethod.Get)
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new
                    {
                        id = "mohist/default",
                        name = "Mohist Default",
                        about = "Standard three-voice PRD with acceptance criteria and non-goals.",
                        isDefault = true,
                        suitableFor = new[] { "feature development" },
                        source = "builtin",
                        defaults = new
                        {
                            labels = new Dictionary<string, string>(),
                            risk = "medium",
                            workflow = "mohist/local",
                        },
                        sections = new[]
                        {
                            new
                            {
                                title = "User Voice",
                                guidance = "Describe the user need.",
                                placeholder = "<user need>",
                            },
                            new
                            {
                                title = "Product Shape",
                                guidance = "Describe the product boundary.",
                                placeholder = "<product shape>",
                            },
                            new
                            {
                                title = "Domain Model",
                                guidance = "Describe the domain model.",
                                placeholder = "<domain model>",
                            },
                            new
                            {
                                title = "Acceptance Criteria",
                                guidance = "List observable outcomes.",
                                placeholder = "- [ ] criterion",
                            },
                            new
                            {
                                title = "Non-Goals",
                                guidance = "List explicitly out-of-scope items.",
                                placeholder = "- non-goal",
                            },
                        },
                    },
                });
            }
            return RecordingHttpHandler.Json(new { success = true, data = new { } });
        });
        var handler = new RecordingHttpHandler(async (req, _) =>
        {
            var r = responder is null ? defaultResponder(req) : responder(req);
            return await Task.FromResult(r);
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:3456") };
        var output = new StringWriter();
        var error = new StringWriter();
        var fs = new FakeFileSystem();
        if (activeProjectId is not null)
        {
            fs.AddFile(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mohist", "cli-state.json"),
                $"{{\"activeProjectId\":\"{activeProjectId}\"}}");
        }
        return (handler, http, output, error, fs, new FakeCommandExecutor());
    }

    [Fact]
    public async Task TemplateList_HitsIssueTemplatesListEndpoint()
    {
        var (handler, http, output, error, fs, executor) = CreateHarness();

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
        var (handler, http, output, error, fs, executor) = CreateHarness();

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
        var (handler, http, output, error, fs, executor) = CreateHarness(activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "template", "list"], output, error, fs, executor);

        Assert.Equal(1, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("mo project use", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TemplateList_WithExplicitProject_ResolvesProject()
    {
        var (handler, http, output, error, fs, executor) = CreateHarness();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "template", "list", "--project", "proj_xyz"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var req = handler.Requests.Last();
        Assert.Equal("/api/issue-templates?projectId=proj_xyz", req.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task TemplateList_Table_RendersEachTemplateByName()
    {
        var (handler, http, output, error, fs, executor) = CreateHarness();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "template", "list", "-o", "table"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("name", text);
        Assert.Contains("Mohist Default", text);
        Assert.Contains("Team Bugfix", text);
        Assert.Contains("builtin", text);
        Assert.Contains("custom", text);
        Assert.Contains("yes", text);
    }

    [Fact]
    public async Task TemplateList_Json_PassesThroughServerEnvelope()
    {
        var (handler, http, output, error, fs, executor) = CreateHarness();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "template", "list"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var json = JsonNode.Parse(output.ToString()) as JsonArray;
        Assert.NotNull(json);
        Assert.Equal(2, json!.Count);
        var ids = json.Select(n => n?["id"]?.GetValue<string>()).ToArray();
        Assert.Contains("mohist/default", ids);
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
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mohist", "cli-state.json"),
            "{\"activeProjectId\":\"proj_abc\"}");
        var executor = new FakeCommandExecutor();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "template", "list", "-o", "table"], output, error, fs, executor);

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
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mohist", "cli-state.json"),
            "{\"activeProjectId\":\"proj_abc\"}");
        var executor = new FakeCommandExecutor();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "template", "list"], output, error, fs, executor);

        Assert.Equal(1, exitCode);
        Assert.Contains("boom", error.ToString());
    }

    [Fact]
    public async Task TemplateGet_DefaultTemplate_HitsCatchAllPath()
    {
        var (handler, http, output, error, fs, executor) = CreateHarness();

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
                    about = "Bugfix template.",
                    isDefault = false,
                    suitableFor = new[] { "bug fixes" },
                    source = "custom",
                    defaults = new
                    {
                        labels = new Dictionary<string, string>(),
                        risk = (string?)null,
                        workflow = (string?)null,
                    },
                    sections = new[]
                    {
                        new
                        {
                            title = "Bug Summary",
                            guidance = "What broke and how to reproduce.",
                            placeholder = "<bug summary>",
                        },
                    },
                },
            }));
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:3456") };
        var output = new StringWriter();
        var error = new StringWriter();
        var fs = new FakeFileSystem();
        fs.AddFile(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mohist", "cli-state.json"),
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
        var (handler, http, output, error, fs, executor) = CreateHarness(activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "template", "get", "mohist/default"], output, error, fs, executor);

        Assert.Equal(1, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("mo project use", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TemplateGet_Table_DisplaysMetadataAndSectionGuidance()
    {
        var (handler, http, output, error, fs, executor) = CreateHarness();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "template", "get", "mohist/default", "-o", "table"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("id:", text);
        Assert.Contains("mohist/default", text);
        Assert.Contains("Mohist Default", text);
        Assert.Contains("default:", text);
        Assert.Contains("source:", text);
        Assert.Contains("builtin", text);
        Assert.Contains("sections:", text);
        Assert.Contains("[1]", text);
        Assert.Contains("User Voice", text);
        Assert.Contains("Describe the user need.", text);
        Assert.Contains("[2]", text);
        Assert.Contains("Product Shape", text);
        Assert.Contains("[5]", text);
        Assert.Contains("Non-Goals", text);
        Assert.Contains("placeholder:", text);
        Assert.Contains("<user need>", text);
        Assert.Contains("guidance:", text);
    }

    [Fact]
    public async Task TemplateGet_DefaultTemplate_PrintsAllFiveSectionTitlesInOrder()
    {
        var (handler, http, output, error, fs, executor) = CreateHarness();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "template", "get", "mohist/default", "-o", "table"], output, error, fs, executor);

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
    public async Task TemplateGet_DefaultsBlock_RendersRiskAndWorkflow()
    {
        var (handler, http, output, error, fs, executor) = CreateHarness();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "template", "get", "mohist/default", "-o", "table"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("defaults.risk:       medium", text);
        Assert.Contains("defaults.workflow:   mohist/local", text);
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
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mohist", "cli-state.json"),
            "{\"activeProjectId\":\"proj_abc\"}");
        var executor = new FakeCommandExecutor();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "template", "get", "nonexistent"], output, error, fs, executor);

        Assert.Equal(4, exitCode);
        Assert.Contains("not found", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TemplateGet_Json_PassesThroughServerEnvelope()
    {
        var (handler, http, output, error, fs, executor) = CreateHarness();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "template", "get", "mohist/default"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var json = JsonNode.Parse(output.ToString()) as JsonObject;
        Assert.NotNull(json);
        Assert.Equal("mohist/default", json!["id"]?.GetValue<string>());
        Assert.Equal("Mohist Default", json["name"]?.GetValue<string>());
        var sections = json["sections"] as JsonArray;
        Assert.NotNull(sections);
        Assert.Equal(5, sections!.Count);
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
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mohist", "cli-state.json"),
            "{\"activeProjectId\":\"proj_abc\"}");
        var executor = new FakeCommandExecutor();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "template", "get", "mohist/default"], output, error, fs, executor);

        Assert.Equal(1, exitCode);
        Assert.Contains("mo server start", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TemplateList_InvalidOutputMode_ReturnsExitCodeOne()
    {
        var (handler, http, output, error, fs, executor) = CreateHarness();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "template", "list", "-o", "yaml"], output, error, fs, executor);

        Assert.Equal(1, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("table", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("json", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}
