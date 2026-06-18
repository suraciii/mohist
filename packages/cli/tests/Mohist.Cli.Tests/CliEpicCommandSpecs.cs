using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public class CliEpicCommandSpecs
{
    private const string ActiveProjectId = "proj_test";

    private static (HttpClient http, RecordingHttpHandler handler, StringWriter output, StringWriter error, FakeFileSystem fileSystem, FakeCommandExecutor executor) SetupEnv(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder,
        string? activeProjectId = ActiveProjectId)
    {
        var handler = new RecordingHttpHandler(responder);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:3456") };
        var output = new StringWriter();
        var error = new StringWriter();
        var fileSystem = new FakeFileSystem();
        if (activeProjectId is not null)
        {
            fileSystem.AddFile(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mohist", "cli-state.json"),
                $"{{\"activeProjectId\":\"{activeProjectId}\"}}");
        }
        var executor = new FakeCommandExecutor();
        return (http, handler, output, error, fileSystem, executor);
    }

    [Fact]
    public async Task EpicList_EmptyProject_PrintsNoEpicsState()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = Array.Empty<object>(),
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["epic", "list", "-o", "table"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"/api/projects/{ActiveProjectId}/epics/", request.RequestUri?.PathAndQuery);
        Assert.Contains("No epics", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EpicList_NonEmpty_PrintsTableHeadersAndRows()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new[]
                {
                    new { id = "epic_aaa", number = 1, title = "Ship MVP", status = "active", priority = "p1" },
                    new { id = "epic_bbb", number = 8, title = "Labels", status = "active", priority = "p2" },
                },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["epic", "list", "-o", "table"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("number", stdout, StringComparison.Ordinal);
        Assert.Contains("title", stdout, StringComparison.Ordinal);
        Assert.Contains("status", stdout, StringComparison.Ordinal);
        Assert.Contains("priority", stdout, StringComparison.Ordinal);
        Assert.Contains("Ship MVP", stdout, StringComparison.Ordinal);
        Assert.Contains("Labels", stdout, StringComparison.Ordinal);
        var request = handler.Requests.Single();
        Assert.Equal($"/api/projects/{ActiveProjectId}/epics/", request.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task EpicCreate_MissingTitle_FailsWithoutCallingApi()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            throw new InvalidOperationException("API must not be called when title is missing"));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["epic", "create", "--description", "desc"], output, error, fileSystem, executor);

        Assert.Equal(1, exitCode);
        Assert.True(
            error.ToString().Contains("title", StringComparison.OrdinalIgnoreCase)
            || output.ToString().Contains("title", StringComparison.OrdinalIgnoreCase),
            "Expected usage/help/error text referencing the title argument to appear");
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task EpicLink_DuplicateMembership_SurfacesConflictCode()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.JsonError(
                "Issue already belongs to another epic",
                "DUPLICATE_EPIC_MEMBERSHIP",
                HttpStatusCode.Conflict)));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["epic", "link", "8", "5"], output, error, fileSystem, executor);

        Assert.Equal(1, exitCode);
        Assert.Contains("DUPLICATE_EPIC_MEMBERSHIP", error.ToString(), StringComparison.Ordinal);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"/api/projects/{ActiveProjectId}/epics/8/issues", request.RequestUri?.PathAndQuery);
        var body = JsonNode.Parse(request.Body!);
        Assert.Equal("5", body!["issueId"]?.GetValue<string>());
    }

    [Fact]
    public async Task EpicDone_NotReady_SurfacesConflictCode()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.JsonError(
                "Epic has 2 undelivered linked issues",
                "EPIC_NOT_READY_TO_MARK_DONE",
                HttpStatusCode.Conflict)));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["epic", "done", "8"], output, error, fileSystem, executor);

        Assert.Equal(1, exitCode);
        Assert.Contains("EPIC_NOT_READY_TO_MARK_DONE", error.ToString(), StringComparison.Ordinal);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"/api/projects/{ActiveProjectId}/epics/8/done", request.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task EpicShow_ByNumber_HitsEpicsEndpointNotIssues()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    id = "epic_labels",
                    number = 8,
                    title = "Labels",
                    status = "active",
                    priority = "p2",
                    description = "Issue classification system",
                    linkedIssues = Array.Empty<object>(),
                    progress = new { deliveredCount = 0, totalIssueCount = 0, nextIssue = (object?)null, readyToMarkDone = true },
                },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["epic", "show", "8"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"/api/projects/{ActiveProjectId}/epics/8", request.RequestUri?.PathAndQuery);
        Assert.DoesNotContain("/issues/8", request.RequestUri!.PathAndQuery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EpicLink_TableConflict_SurfacesConflictCode()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.JsonError(
                "Issue already belongs to epic 3",
                "DUPLICATE_EPIC_MEMBERSHIP",
                HttpStatusCode.Conflict)));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["epic", "link", "8", "5", "-o", "table"], output, error, fileSystem, executor);

        Assert.Equal(1, exitCode);
        Assert.Contains("DUPLICATE_EPIC_MEMBERSHIP", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(output.ToString());
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task EpicDone_TableConflict_SurfacesConflictCode()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.JsonError(
                "Epic has undelivered linked issues",
                "EPIC_NOT_READY_TO_MARK_DONE",
                HttpStatusCode.Conflict)));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["epic", "done", "8", "-o", "table"], output, error, fileSystem, executor);

        Assert.Equal(1, exitCode);
        Assert.Contains("EPIC_NOT_READY_TO_MARK_DONE", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(output.ToString());
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task EpicClose_TableAlreadyTerminalConflict_SurfacesConflictCode()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.JsonError(
                "Epic is already terminal",
                "EPIC_ALREADY_TERMINAL",
                HttpStatusCode.Conflict)));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["epic", "close", "8", "-o", "table"], output, error, fileSystem, executor);

        Assert.Equal(1, exitCode);
        Assert.Contains("EPIC_ALREADY_TERMINAL", error.ToString(), StringComparison.Ordinal);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"/api/projects/{ActiveProjectId}/epics/8/close", request.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task EpicUnlink_TableSuccess_PrintsUnlinkedConfirmation()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { epicId = "epic_8", issueId = "issue_5" },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["epic", "unlink", "8", "issue_5", "-o", "table"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        Assert.Contains("Unlinked issue issue_5 from epic epic_8", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Linked issue", output.ToString(), StringComparison.Ordinal);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal($"/api/projects/{ActiveProjectId}/epics/8/issues/issue_5", request.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task EpicUpdate_SendsOnlySuppliedFields()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { id = "epic_8", number = 8, title = "New title", status = "active", priority = "p2" },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["epic", "update", "8", "--title", "New title"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Patch, request.Method);
        Assert.Equal($"/api/projects/{ActiveProjectId}/epics/8", request.RequestUri?.PathAndQuery);
        var body = JsonNode.Parse(request.Body!)!.AsObject();
        Assert.Equal("New title", body["title"]?.GetValue<string>());
        Assert.False(body.ContainsKey("description"));
        Assert.False(body.ContainsKey("priority"));
    }

    [Fact]
    public async Task EpicList_ProjectOverride_UsesProjectArgument()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = Array.Empty<object>() })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["epic", "list", "--project", "proj_override", "-o", "table"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        Assert.Equal("/api/projects/proj_override/epics/", handler.Requests.Single().RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task EpicList_ProjectIdOverride_UsesProjectIdArgument()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = Array.Empty<object>() })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["epic", "list", "--project-id", "proj_by_id", "-o", "table"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        Assert.Equal("/api/projects/proj_by_id/epics/", handler.Requests.Single().RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task EpicList_JsonOutput_EmitsEnvelopeVerbatim()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new[] { new { id = "epic_8", number = 8, title = "Labels", status = "active", priority = "p2" } },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["epic", "list", "-o", "json"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        Assert.Contains("\"id\": \"epic_8\"", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("\"title\": \"Labels\"", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("number  title", output.ToString(), StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task EpicList_InvalidOutput_FailsWithoutCallingApi()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            throw new InvalidOperationException("API must not be called when output mode is invalid"));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["epic", "list", "-o", "yaml"], output, error, fileSystem, executor);

        Assert.Equal(1, exitCode);
        Assert.Contains("--output must be 'table' or 'json'", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task EpicHelp_ListsAllSubcommands()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            throw new InvalidOperationException("API must not be called for help"));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["epic", "--help"], output, error, fileSystem, executor);

        var stdout = output.ToString();
        Assert.Equal(0, exitCode);
        foreach (var command in new[] { "list", "create", "show", "update", "link", "unlink", "done", "close" })
            Assert.Contains(command, stdout, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }
}
