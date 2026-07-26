using System.Net;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public class CliAgentJobCommandSpecs
{
    private const string ActiveProjectId = "proj_test";

    private static (HttpClient http, RecordingHttpHandler handler, StringWriter output, StringWriter error, FakeFileSystem fileSystem, FakeCommandExecutor executor) SetupEnv(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(responder, ActiveProjectId);
        return (http, handler, output, error, fs, executor);
    }

    [Fact]
    public async Task JobList_Table_ResolvesAgentAndRendersJobs()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            if (path.EndsWith("/agents?all=true", StringComparison.Ordinal))
            {
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new[] { new { id = "agent_123", name = "reviewer", status = "active" } },
                }));
            }
            return Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new[]
                {
                    new { jobId = "job_1", agentId = "agent_123", agentName = "reviewer", status = "running", submittedAt = "2026-07-25T10:00:00Z", terminalAt = (string?)null },
                    new { jobId = "job_2", agentId = "agent_123", agentName = "reviewer", status = "completed", submittedAt = "2026-07-25T09:00:00Z", terminalAt = (string?)"2026-07-25T09:30:00Z" },
                },
            }));
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "job", "list", "reviewer"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal("/api/projects/proj_test/agents?all=true", handler.Requests[0].RequestUri?.PathAndQuery);
        Assert.Equal(HttpMethod.Get, handler.Requests[1].Method);
        Assert.Equal("/api/projects/proj_test/agents/agent_123/jobs", handler.Requests[1].RequestUri?.PathAndQuery);
        var stdout = output.ToString();
        Assert.Contains("job_1", stdout, StringComparison.Ordinal);
        Assert.Contains("job_2", stdout, StringComparison.Ordinal);
        Assert.Contains("running", stdout, StringComparison.Ordinal);
        Assert.Contains("completed", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JobList_AgentWithNoJobs_RendersEmptyNotice()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            if (path.EndsWith("/agents?all=true", StringComparison.Ordinal))
            {
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new[] { new { id = "agent_123", name = "reviewer", status = "active" } },
                }));
            }
            return Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = Array.Empty<object>() }));
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "job", "list", "reviewer"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        Assert.Contains("No agent jobs", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task JobList_UnknownAgent_SurfacesClientError()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            if (path.EndsWith("/agents?all=true", StringComparison.Ordinal))
            {
                return Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = Array.Empty<object>() }));
            }
            return Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = Array.Empty<object>() }));
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "job", "list", "nope"], output, error, fileSystem, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Agent 'nope' not found", error.ToString(), StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task JobView_Table_RendersStatusAndTerminalResultFields()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    jobId = "agent-job-launch-abc",
                    status = "failed",
                    message = "build failed",
                    output = "{\"log\":\"err\"}",
                    artifactUploadIds = new[] { "art-1", "art-2" },
                    failureReason = "runner-unavailable",
                    exitCode = 1,
                },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "job", "view", "agent-job-launch-abc"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/api/projects/proj_test/agent-jobs/agent-job-launch-abc", request.RequestUri?.PathAndQuery);
        var stdout = output.ToString();
        Assert.Contains("agent-job-launch-abc", stdout, StringComparison.Ordinal);
        Assert.Contains("failed", stdout, StringComparison.Ordinal);
        Assert.Contains("build failed", stdout, StringComparison.Ordinal);
        Assert.Contains("runner-unavailable", stdout, StringComparison.Ordinal);
        Assert.Contains("exit code:       1", stdout, StringComparison.Ordinal);
        Assert.Contains("art-1,art-2", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JobView_NonTerminal_ShowsStatusWithoutTerminalRows()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    jobId = "agent-job-launch-pending",
                    status = "running",
                    message = (string?)null,
                    output = (string?)null,
                    artifactUploadIds = Array.Empty<string>(),
                    failureReason = (string?)null,
                    exitCode = (int?)null,
                },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "job", "view", "agent-job-launch-pending"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("running", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("failure reason", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("exit code", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("message", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JobView_UnknownJob_SurfacesServer404()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.JsonError(
                "Job not found", "not_found", HttpStatusCode.NotFound)));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "job", "view", "never-existed"], output, error, fileSystem, executor);

        Assert.Equal(1, exitCode);
        Assert.Contains("Job not found", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(output.ToString());
    }

    [Fact]
    public async Task JobView_SelectedJson_ProjectsRequestedFields()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    jobId = "job-x",
                    status = "completed",
                    message = "ok",
                    output = "{}",
                    artifactUploadIds = new[] { "a" },
                    failureReason = (string?)null,
                    exitCode = 0,
                },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "job", "view", "job-x", "--json", "jobId,status,exitCode"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("\"jobId\": \"job-x\"", stdout, StringComparison.Ordinal);
        Assert.Contains("\"status\": \"completed\"", stdout, StringComparison.Ordinal);
        Assert.Contains("\"exitCode\": 0", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("\"message\"", stdout, StringComparison.Ordinal);
    }
}
