using System.Net;
using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public class CliWorkflowListSpecs
{
    private static (RecordingHttpHandler Handler, HttpClient Http, StringWriter Output, StringWriter Error, FakeFileSystem Fs, FakeCommandExecutor Executor)
        CreateHarness(Func<HttpRequestMessage, HttpResponseMessage>? responder = null)
    {
        var handler = new RecordingHttpHandler((req, _) =>
        {
            var response = responder?.Invoke(req);
            if (response is not null) return Task.FromResult(response);
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

    [Fact]
    public async Task WorkflowListDescribed_WithProject_SendsProjectQueryParam()
    {
        var (handler, http, output, error, fs, executor) = CreateHarness(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri?.PathAndQuery == "/api/workflow-profiles?project=proj_abc")
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new[]
                    {
                        new { id = "mohist/local", displayName = "Mohist Local Workflow", description = "Standard pipeline.", suitableFor = new[] { "default", "feature" } },
                    },
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["workflow", "list", "--described", "--project", "proj_abc"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var getReq = handler.Requests.Last(r => r.Method == HttpMethod.Get);
        Assert.Equal("/api/workflow-profiles?project=proj_abc", getReq.RequestUri?.PathAndQuery);
        var stdout = output.ToString();
        Assert.Contains("mohist/local", stdout);
    }

    [Fact]
    public async Task WorkflowListDescribed_WithProjectIdAlias_SendsProjectQueryParam()
    {
        var (handler, http, output, error, fs, executor) = CreateHarness(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri?.PathAndQuery == "/api/workflow-profiles?project=proj_abc")
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new[]
                    {
                        new { id = "mohist/local", displayName = "Mohist Local Workflow", description = "Standard pipeline.", suitableFor = new[] { "default", "feature" } },
                    },
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["workflow", "list", "--described", "--project-id", "proj_abc"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var getReq = handler.Requests.Last(r => r.Method == HttpMethod.Get);
        Assert.Equal("/api/workflow-profiles?project=proj_abc", getReq.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task WorkflowListDescribed_WithActiveProject_SendsProjectQueryParam()
    {
        var (handler, http, output, error, fs, executor) = CreateHarness(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri?.PathAndQuery == "/api/workflow-profiles?project=proj_abc")
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new[]
                    {
                        new { id = "mohist/local", displayName = "Mohist Local Workflow", description = "Standard pipeline.", suitableFor = new[] { "default", "feature" } },
                        new { id = "mohist/github-pr", displayName = "GitHub PR Workflow", description = "PR pipeline.", suitableFor = new[] { "pr", "review" } },
                    },
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["workflow", "list", "--described"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var getReq = handler.Requests.Last(r => r.Method == HttpMethod.Get);
        Assert.Equal("/api/workflow-profiles?project=proj_abc", getReq.RequestUri?.PathAndQuery);
        var stdout = output.ToString();
        Assert.Contains("mohist/local", stdout);
        Assert.Contains("mohist/github-pr", stdout);
    }

    [Fact]
    public async Task WorkflowListDescribed_NoProjectResolvable_FallsBackToUnfilteredWithStderrNote()
    {
        var (handler, http, output, error, fs, executor) = CreateHarness(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri?.PathAndQuery == "/api/workflow-profiles")
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new[]
                    {
                        new { id = "mohist/local", displayName = "Mohist Local Workflow", description = "Standard pipeline.", suitableFor = new[] { "default", "feature" } },
                    },
                });
            }
            return null!;
        });
        // No cli-state.json — no active project
        var fs2 = new FakeFileSystem();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["workflow", "list", "--described"],
            output, error, fs2, executor);

        Assert.Equal(0, exitCode);
        var getReq = handler.Requests.Last(r => r.Method == HttpMethod.Get);
        Assert.Equal("/api/workflow-profiles", getReq.RequestUri?.PathAndQuery);
        var stderr = error.ToString();
        Assert.Contains("degraded", stderr);
    }

    [Fact]
    public async Task WorkflowListDescribed_WithConflictingProjectFlags_ReturnsOneAndDoesNotRequestDiscovery()
    {
        var (handler, http, output, error, fs, executor) = CreateHarness();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["workflow", "list", "--described", "--project", "proj_a", "--project-id", "proj_b"],
            output, error, fs, executor);

        Assert.Equal(1, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("--project and --project-id resolve to different values", error.ToString());
        Assert.DoesNotContain("degraded", error.ToString());
    }

    [Fact]
    public async Task WorkflowListPlain_WithProject_SendsProjectQueryParam()
    {
        var (handler, http, output, error, fs, executor) = CreateHarness(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri?.PathAndQuery == "/api/workflow-templates/system?project=proj_abc")
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new[]
                    {
                        new { id = "mohist/local" },
                        new { id = "mohist/github-pr" },
                    },
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["workflow", "list", "--project", "proj_abc"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var getReq = handler.Requests.Last(r => r.Method == HttpMethod.Get);
        Assert.Equal("/api/workflow-templates/system?project=proj_abc", getReq.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task WorkflowListPlain_WithActiveProject_SendsProjectQueryParam()
    {
        var (handler, http, output, error, fs, executor) = CreateHarness(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri?.PathAndQuery == "/api/workflow-templates/system?project=proj_abc")
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new[]
                    {
                        new { id = "mohist/local" },
                    },
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["workflow", "list"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var getReq = handler.Requests.Last(r => r.Method == HttpMethod.Get);
        Assert.Equal("/api/workflow-templates/system?project=proj_abc", getReq.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task WorkflowListPlain_WithConflictingProjectFlags_ReturnsOneAndDoesNotRequestDiscovery()
    {
        var (handler, http, output, error, fs, executor) = CreateHarness();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["workflow", "list", "--project", "proj_a", "--project-id", "proj_b"],
            output, error, fs, executor);

        Assert.Equal(1, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("--project and --project-id resolve to different values", error.ToString());
    }

    [Fact]
    public async Task WorkflowListDescribed_WithProject_ExcludesDisabledProfile()
    {
        var (handler, http, output, error, fs, executor) = CreateHarness(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri?.PathAndQuery == "/api/workflow-profiles?project=proj_abc")
            {
                // Server already filters — return only enabled profiles
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new[]
                    {
                        new { id = "mohist/local", displayName = "Mohist Local Workflow", description = "Standard pipeline.", suitableFor = new[] { "default", "feature" } },
                    },
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["workflow", "list", "--described", "--project", "proj_abc"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("mohist/local", stdout);
        Assert.DoesNotContain("mohist/github-pr", stdout);
    }

    [Fact]
    public async Task WorkflowListDescribed_WithMissingProject_ReturnsNotFoundAndPrintsError()
    {
        var (handler, http, output, error, fs, executor) = CreateHarness(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri?.PathAndQuery == "/api/workflow-profiles?project=missing-project")
            {
                return RecordingHttpHandler.JsonError(
                    "Project 'missing-project' not found",
                    "project_not_found",
                    HttpStatusCode.NotFound);
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["workflow", "list", "--described", "--project", "missing-project"],
            output, error, fs, executor);

        Assert.Equal(4, exitCode);
        var getReq = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Get);
        Assert.Equal("/api/workflow-profiles?project=missing-project", getReq.RequestUri?.PathAndQuery);
        Assert.Contains("Project 'missing-project' not found", error.ToString());
        Assert.Contains("project_not_found", error.ToString());
        Assert.DoesNotContain("degraded", error.ToString());
        Assert.Empty(output.ToString());
    }
}
