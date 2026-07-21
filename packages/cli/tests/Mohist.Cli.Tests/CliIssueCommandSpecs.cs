using System.Net;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public class CliIssueCommandSpecs
{
    private const string ActiveProjectId = "proj_test";

    public static IEnumerable<object[]> RemovedRepositoryOptionCases()
    {
        yield return [new[] { "issue", "create", "Title", "--repository", "web" }];
        yield return [new[] { "issue", "update", "1", "--repository", "web" }];
        yield return [new[] { "issue", "list", "--repository", "web" }];
        yield return [new[] { "issue", "show", "1", "--repository", "web" }];
    }

    [Theory]
    [MemberData(nameof(RemovedRepositoryOptionCases))]
    public async Task IssueCommands_RepositoryOption_IsRejectedWithoutDispatch(string[] args)
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            throw new InvalidOperationException("Issue request must not be sent"));

        var exitCode = await MohistCliCommands.RunAsync(
            http, args, output, error, fileSystem, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("--repository", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task IssueList_RepoFilter_SendsRepositoryQuery()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = Array.Empty<object>() })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "list", "--repo", "SERVER"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        Assert.Equal("/api/projects/proj_test/issues?repository=SERVER", handler.Requests.Single().RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task IssueList_ParentFilter_SendsParentQuery()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = Array.Empty<object>() })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "list", "--parent", "42"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        Assert.Equal("/api/projects/proj_test/issues?parent=42", handler.Requests.Single().RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task IssueList_Table_RendersStoredRepositoryWithoutMetadata()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new[]
                {
                    new
                    {
                        number = 7,
                        title = "Historical target",
                        repositoryName = "web",
                        workflowStage = "done",
                        status = "done",
                        priority = "p2",
                    },
                },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "list", "--output", "table"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        Assert.Contains("repository", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("web", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task IssueShow_Table_RendersStoredRepositoryWithoutMetadata()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    number = 7,
                    title = "Historical target",
                    repositoryName = "web",
                    workflowStage = "done",
                    status = "done",
                    priority = "p2",
                },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "show", "7", "--output", "table"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        Assert.Contains("repository: web", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task IssueEvents_Json_PreservesRoutedSessionClosedOutcome()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new[]
                {
                    new
                    {
                        id = 9,
                        eventId = "session-1:closed:agent-job:job-1:terminal",
                        source = "/mohist/agent-session/session-1",
                        type = "session.closed",
                        data = new
                        {
                            status = "failed",
                            failureReason = "workspace unavailable",
                            triggerEventId = "evt-1",
                            triggerRuleId = "rule-1",
                        },
                    },
                },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "events", "42"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        Assert.Equal("/api/projects/proj_test/issues/42/events", handler.Requests.Single().RequestUri?.PathAndQuery);
        var stdout = output.ToString();
        Assert.Contains("session-1:closed:agent-job:job-1:terminal", stdout, StringComparison.Ordinal);
        Assert.Contains("workspace unavailable", stdout, StringComparison.Ordinal);
        Assert.Contains("evt-1", stdout, StringComparison.Ordinal);
        Assert.Contains("rule-1", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArchiveAllCompleted_Table_PrintsServerResult()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    archived = 5,
                    skipped = 2,
                    skippedNumbers = new[] { 7, 9 },
                    message = "Archived 5 completed issues, skipped 2",
                },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "archive", "--all-completed"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"/api/projects/{ActiveProjectId}/issues/archive-completed", request.RequestUri?.PathAndQuery);
        Assert.Equal("{}", request.Body);
        Assert.Contains("Archived 5 completed issues, skipped 2", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArchiveAllCompleted_Json_EmitsRawServerResponse()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    archived = 3,
                    skipped = 0,
                    skippedNumbers = Array.Empty<int>(),
                    message = "Archived 3 completed issues, skipped 0",
                },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "archive", "--all-completed", "-o", "json"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("\"success\": true", stdout, StringComparison.Ordinal);
        Assert.Contains("\"archived\": 3", stdout, StringComparison.Ordinal);
        Assert.Contains("\"message\": \"Archived 3 completed issues, skipped 0\"", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArchiveAllCompleted_NoProject_FailsClearly()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            throw new InvalidOperationException("API must not be called with no project"),
            activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "archive", "--all-completed"], output, error, fileSystem, executor);

        Assert.Equal(1, exitCode);
        Assert.Contains(MohistCliCommands.NoActiveProjectMessage, error.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ArchiveAllCompleted_MutuallyExclusiveWithNumber_FailsClearly()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            throw new InvalidOperationException("API must not be called when mutually exclusive args are passed"));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "archive", "42", "--all-completed"], output, error, fileSystem, executor);

        Assert.Equal(1, exitCode);
        Assert.Contains("mutually exclusive", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ArchiveWithoutNumberOrAllCompleted_FailsClearlyWithoutCallingApi()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            throw new InvalidOperationException("API must not be called when archive target is missing"));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "archive"], output, error, fileSystem, executor);

        Assert.Equal(1, exitCode);
        Assert.Contains("<number> is required", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ArchiveSingleIssue_StillArchivesNumber()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = new { } })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "archive", "42"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"/api/projects/{ActiveProjectId}/issues/42/archive", request.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task ArchiveAllCompleted_InvalidOutput_FailsWithoutCallingApi()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            throw new InvalidOperationException("API must not be called when output mode is invalid"));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "archive", "--all-completed", "-o", "yaml"], output, error, fileSystem, executor);

        Assert.Equal(1, exitCode);
        Assert.Contains("--output must be 'table' or 'json'", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ArchiveAllCompleted_ProjectIdOverride_UsesProjectIdArgument()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { archived = 1, skipped = 0, skippedNumbers = Array.Empty<int>(), message = "Archived 1 completed issues" },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "archive", "--all-completed", "--project-id", "proj_by_id"],
            output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        Assert.Equal("/api/projects/proj_by_id/issues/archive-completed", handler.Requests.Single().RequestUri?.PathAndQuery);
    }

    private static (HttpClient http, RecordingHttpHandler handler, StringWriter output, StringWriter error, FakeFileSystem fileSystem, FakeCommandExecutor executor) SetupEnv(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder,
        string? activeProjectId = ActiveProjectId)
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(responder, activeProjectId);
        return (http, handler, output, error, fs, executor);
    }
}
