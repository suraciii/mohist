using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Mohist.Cli;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Specs.Api;

public class IssueCliTableRendererSpecs
{
    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task PrintWithOutputAsync_Table_SendsSameHttpRequestAsJson()
    {
        var jsonHandler = BuildHandler("""
            { "success": true, "data": [{ "id": "proj_1", "name": "mohist-local", "baseBranch": "master" }] }
            """);
        var tableHandler = BuildHandler("""
            { "success": true, "data": [{ "id": "proj_1", "name": "mohist-local", "baseBranch": "master" }] }
            """);

        var jsonApi = BuildApi(jsonHandler);
        await jsonApi.PrintWithOutputAsync("/api/projects", "json");

        var tableApi = BuildApi(tableHandler);
        await tableApi.PrintWithOutputAsync("/api/projects", "table", "ProjectList");

        var jsonReq = jsonHandler.Requests.Single();
        var tableReq = tableHandler.Requests.Single();

        Assert.Equal(HttpMethod.Get, jsonReq.Method);
        Assert.Equal(HttpMethod.Get, tableReq.Method);
        Assert.Equal("/api/projects", jsonReq.RequestUri!.PathAndQuery);
        Assert.Equal("/api/projects", tableReq.RequestUri!.PathAndQuery);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task RenderTable_ProjectList_ContainsIdNameBaseBranch_AndMarksActive()
    {
        var data = JsonNode.Parse("""
            [
              { "id": "proj_aaa", "name": "alpha", "baseBranch": "main" },
              { "id": "proj_bbb", "name": "beta",  "baseBranch": "dev" }
            ]
            """);

        var output = new StringWriter();
        var fs = new FakeFileSystem();
        await fs.WriteAllTextAsync(
            "/home/test/.mohist/cli-state.json",
            """{ "activeProjectId": "proj_bbb" }""");
        var api = new MohistCliApi(
            new HttpClient(new HttpClientHandler()) { BaseAddress = new Uri("http://localhost:3456") },
            output,
            new StringWriter(),
            fs,
            new NoopCommandExecutor());

        await api.RenderTableAsync(data, MohistCliApi.TableShape.ProjectList);

        var text = output.ToString();
        Assert.Contains("id", text);
        Assert.Contains("name", text);
        Assert.Contains("base branch", text);
        Assert.Contains("alpha", text);
        Assert.Contains("beta", text);
        Assert.Contains("proj_bbb", text);
        Assert.Contains("*", text);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task RenderTable_ProjectShow_IsMultiLineSummary()
    {
        var data = JsonNode.Parse("""
            {
              "id": "proj_x",
              "name": "demo",
              "baseBranch": "master",
              "repositories": [{ "name": "main" }, { "name": "alt" }],
              "createdAt": "2026-01-01T00:00:00Z",
              "updatedAt": "2026-02-01T00:00:00Z"
            }
            """);
        var output = new StringWriter();
        var api = new MohistCliApi(
            new HttpClient(new HttpClientHandler()) { BaseAddress = new Uri("http://localhost:3456") },
            output,
            new StringWriter(),
            new FakeFileSystem(),
            new NoopCommandExecutor());

        await api.RenderTableAsync(data, MohistCliApi.TableShape.ProjectShow);

        var text = output.ToString();
        Assert.Contains("id:", text);
        Assert.Contains("name:", text);
        Assert.Contains("base branch:", text);
        Assert.Contains("repositories:", text);
        Assert.Contains("2", text);
        Assert.Contains("created:", text);
        Assert.Contains("updated:", text);
        Assert.Contains("demo", text);
        Assert.Contains("proj_x", text);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task RenderTable_IssueList_ContainsNumberTitleStageStatusPriority_TruncatesLongTitle()
    {
        var longTitle = new string('x', 120);
        var data = JsonNode.Parse($$"""
            [
              { "number": 1, "title": "{{longTitle}}", "workflowStage": "build", "status": "in_progress", "priority": "p1" },
              { "number": 2, "title": "short", "workflowStage": "plan", "status": "backlog", "priority": "p3" }
            ]
            """);

        var output = new StringWriter();
        var api = new MohistCliApi(
            new HttpClient(new HttpClientHandler()) { BaseAddress = new Uri("http://localhost:3456") },
            output,
            new StringWriter(),
            new FakeFileSystem(),
            new NoopCommandExecutor());

        await api.RenderTableAsync(data, MohistCliApi.TableShape.IssueList);

        var text = output.ToString();
        Assert.Contains("number", text);
        Assert.Contains("title", text);
        Assert.Contains("stage", text);
        Assert.Contains("status", text);
        Assert.Contains("priority", text);
        Assert.Contains("build", text);
        Assert.Contains("in_progress", text);
        Assert.Contains("p1", text);
        Assert.Contains("…", text);
        Assert.DoesNotContain(longTitle, text);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task RenderTable_IssueShow_IsMultiLineSummaryWithCondensedBody()
    {
        var longBody = new string('B', 200);
        var data = JsonNode.Parse($$"""
            {
              "number": 83,
              "title": "Expand Mohist CLI ergonomics for project-scoped work",
              "workflowStage": "build",
              "status": "in_progress",
              "priority": "p1",
              "projectName": "mohist-local",
              "updatedAt": "2026-06-11T08:34:11.158Z",
              "body": "{{longBody}}"
            }
            """);

        var output = new StringWriter();
        var api = new MohistCliApi(
            new HttpClient(new HttpClientHandler()) { BaseAddress = new Uri("http://localhost:3456") },
            output,
            new StringWriter(),
            new FakeFileSystem(),
            new NoopCommandExecutor());

        await api.RenderTableAsync(data, MohistCliApi.TableShape.IssueShow);

        var text = output.ToString();
        Assert.Contains("number:", text);
        Assert.Contains("title:", text);
        Assert.Contains("stage:", text);
        Assert.Contains("status:", text);
        Assert.Contains("priority:", text);
        Assert.Contains("project:", text);
        Assert.Contains("updated:", text);
        Assert.Contains("body:", text);
        Assert.Contains("mohist-local", text);
        Assert.Contains("…", text);
        Assert.DoesNotContain(longBody, text);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task RenderTable_WorkflowStatus_SummarizesCurrentStageTaskStatesAndWaiting()
    {
        var data = JsonNode.Parse("""
            {
              "issueId": "iss_83",
              "issueNumber": 83,
              "title": "Expand Mohist CLI ergonomics",
              "stage": "check",
              "runtimeStatus": "running",
              "workflowRunId": "wr_83",
              "workflow": {
                "workflowRunId": "wr_83",
                "status": "running",
                "currentStage": "build",
                "stages": [
                  { "stage": "plan",  "status": "completed", "tasks": [ { "status": "completed" }, { "status": "completed" } ], "approvalStatus": { "result": "approved" } },
                  { "stage": "build", "status": "running",   "tasks": [ { "status": "completed" }, { "status": "pending" } ], "approvalStatus": null },
                  { "stage": "check", "status": "pending",   "tasks": [], "approvalStatus": null }
                ]
              }
            }
            """);

        var output = new StringWriter();
        var api = new MohistCliApi(
            new HttpClient(new HttpClientHandler()) { BaseAddress = new Uri("http://localhost:3456") },
            output,
            new StringWriter(),
            new FakeFileSystem(),
            new NoopCommandExecutor());

        await api.RenderTableAsync(data, MohistCliApi.TableShape.WorkflowStatus);

        var text = output.ToString();
        Assert.Contains("current stage: build", text);
        Assert.Contains("status:        running", text);
        Assert.Contains("plan", text);
        Assert.Contains("approved", text);
        Assert.Contains("pending", text);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task RenderTable_Sessions_ListsIdStateStartedModel()
    {
        var data = JsonNode.Parse("""
            [
              { "sessionName": "T-006.1", "status": "running",   "createdAt": "2026-06-11T08:34:11Z", "model": "minimax-coding-plan/MiniMax-M3" },
              { "sessionName": "T-005.1", "status": "completed", "createdAt": "2026-06-11T08:00:00Z", "model": "minimax-coding-plan/MiniMax-M3" }
            ]
            """);

        var output = new StringWriter();
        var api = new MohistCliApi(
            new HttpClient(new HttpClientHandler()) { BaseAddress = new Uri("http://localhost:3456") },
            output,
            new StringWriter(),
            new FakeFileSystem(),
            new NoopCommandExecutor());

        await api.RenderTableAsync(data, MohistCliApi.TableShape.Sessions);

        var text = output.ToString();
        Assert.Contains("id", text);
        Assert.Contains("state", text);
        Assert.Contains("started", text);
        Assert.Contains("model", text);
        Assert.Contains("T-006.1", text);
        Assert.Contains("running", text);
        Assert.Contains("completed", text);
        Assert.Contains("minimax-coding-plan/MiniMax-M3", text);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task RenderTable_RepoList_ListsNamePathRemoteBaseBranchAndIsDefault()
    {
        var data = JsonNode.Parse("""
            [
              { "name": "master", "path": "/home/repo", "remote": "git@example.com:repo.git", "baseBranch": "master", "isDefault": true },
              { "name": "alt",    "path": "/tmp/alt",   "remote": null,                            "baseBranch": "main",   "isDefault": false }
            ]
            """);

        var output = new StringWriter();
        var api = new MohistCliApi(
            new HttpClient(new HttpClientHandler()) { BaseAddress = new Uri("http://localhost:3456") },
            output,
            new StringWriter(),
            new FakeFileSystem(),
            new NoopCommandExecutor());

        await api.RenderTableAsync(data, MohistCliApi.TableShape.RepoList);

        var text = output.ToString();
        Assert.Contains("name", text);
        Assert.Contains("path", text);
        Assert.Contains("remote", text);
        Assert.Contains("base branch", text);
        Assert.Contains("default", text);
        Assert.Contains("master", text);
        Assert.Contains("alt", text);
        Assert.Contains("git@example.com:repo.git", text);
        Assert.Contains("yes", text);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void Truncate_RespectsSoftCaps_60And24()
    {
        var long60 = new string('a', 80);
        var truncated60 = InvokeTruncate(long60, 60);
        Assert.Equal(60, truncated60.Length);
        Assert.EndsWith("…", truncated60);

        var long24 = new string('b', 40);
        var truncated24 = InvokeTruncate(long24, 24);
        Assert.Equal(24, truncated24.Length);
        Assert.EndsWith("…", truncated24);

        var shortValue = "ok";
        Assert.Equal("ok", InvokeTruncate(shortValue, 60));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void Truncate_OnlyFirstLineIsKept()
    {
        var multiline = "first line\nsecond line that should be discarded";
        var result = InvokeTruncate(multiline, 60);
        Assert.Equal("first line", result);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void ParseTableShape_AcceptsKnownAndDefaultsOnUnknown()
    {
        Assert.Equal(MohistCliApi.TableShape.ProjectList, MohistCliApi.ParseTableShape(null));
        Assert.Equal(MohistCliApi.TableShape.ProjectList, MohistCliApi.ParseTableShape(""));
        Assert.Equal(MohistCliApi.TableShape.ProjectList, MohistCliApi.ParseTableShape("Unknown"));
        Assert.Equal(MohistCliApi.TableShape.IssueList, MohistCliApi.ParseTableShape("IssueList"));
        Assert.Equal(MohistCliApi.TableShape.RepoList, MohistCliApi.ParseTableShape("RepoList"));
    }

    private static string InvokeTruncate(string value, int softCap)
    {
        var mi = typeof(TableRenderer).GetMethod(
            "Truncate",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        return (string)mi!.Invoke(null, new object[] { value, softCap })!;
    }

    private static RecordingHandler BuildHandler(string json) =>
        new(HttpStatusCode.OK, json);

    private static MohistCliApi BuildApi(RecordingHandler handler) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost:3456") },
            new StringWriter(),
            new StringWriter(),
            new FakeFileSystem(),
            new NoopCommandExecutor());

    private sealed class NoopCommandExecutor : ICommandExecutor
    {
        public Task<(int ExitCode, string Stdout, string Stderr)> ExecuteAsync(string fileName, string[] args, string? workingDirectory = null) =>
            Task.FromResult((0, "", ""));
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;
        public List<HttpRequestMessage> Requests { get; } = new();

        public RecordingHandler(HttpStatusCode status, string json)
        {
            _response = new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_response);
        }
    }
}
