using System.Net;
using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public class CliIssueWatchSpecs
{
    private const string ActiveProjectId = "proj_abc";

    [Fact]
    public async Task IssueWatchAdd_PostsAgentIdToWatchEndpoint()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get
                && req.RequestUri?.PathAndQuery == $"/api/projects/{ActiveProjectId}/agents?all=true")
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new[]
                    {
                        new { id = "agent_alpha", name = "alpha" },
                    },
                });
            }
            if (req.Method == HttpMethod.Post)
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new
                    {
                        id = "issue_201",
                        number = 201,
                        title = "T",
                        watching = new[]
                        {
                            new { agentId = "agent_alpha", state = "watching", createdAt = "2026-01-01T00:00:00Z", updatedAt = "2026-01-01T00:00:00Z" },
                        },
                        muted = Array.Empty<object>(),
                    },
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "watch", "add", "201", "--agent", "alpha"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var postReq = handler.Requests.Last(r => r.Method == HttpMethod.Post);
        Assert.Equal($"/api/projects/{ActiveProjectId}/issues/201/watch", postReq.RequestUri?.PathAndQuery);
        var body = JsonNode.Parse(postReq.Body!)!;
        Assert.Equal("agent_alpha", body["agentId"]?.GetValue<string>());
    }

    [Fact]
    public async Task IssueWatchRemove_SendsDeleteWithAgentId()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get
                && req.RequestUri?.PathAndQuery == $"/api/projects/{ActiveProjectId}/agents?all=true")
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new[]
                    {
                        new { id = "agent_alpha", name = "alpha" },
                    },
                });
            }
            if (req.Method == HttpMethod.Delete)
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new
                    {
                        id = "issue_201",
                        number = 201,
                        title = "T",
                        watching = Array.Empty<object>(),
                        muted = new[]
                        {
                            new { agentId = "agent_alpha", state = "muted", createdAt = "2026-01-01T00:00:00Z", updatedAt = "2026-01-01T00:00:00Z" },
                        },
                    },
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "watch", "remove", "201", "--agent", "alpha"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var deleteReq = handler.Requests.Last(r => r.Method == HttpMethod.Delete);
        Assert.Equal($"/api/projects/{ActiveProjectId}/issues/201/watch", deleteReq.RequestUri?.PathAndQuery);
        var body = JsonNode.Parse(deleteReq.Body!)!;
        Assert.Equal("agent_alpha", body["agentId"]?.GetValue<string>());
    }

    [Fact]
    public async Task IssueWatchAdd_MissingAgentFlag_LocalUsageError()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "watch", "add", "201"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("--agent is required", error.ToString());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task IssueWatchAdd_UnknownAgentName_DoesNotCallServer()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get
                && req.RequestUri?.PathAndQuery == $"/api/projects/{ActiveProjectId}/agents?all=true")
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = Array.Empty<object>(),
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "watch", "add", "201", "--agent", "no-such-agent"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Agent 'no-such-agent' not found", error.ToString());
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task IssueWatchAdd_ArchivedAgentOnServer_SurfacesAgentArchivedCode()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get
                && req.RequestUri?.PathAndQuery == $"/api/projects/{ActiveProjectId}/agents?all=true")
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new[]
                    {
                        new { id = "agent_alpha", name = "alpha" },
                    },
                });
            }
            if (req.Method == HttpMethod.Post)
            {
                return RecordingHttpHandler.Json(
                    new { success = false, error = "Agent 'agent_alpha' is archived.", code = "agent_archived" },
                    HttpStatusCode.Conflict);
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "watch", "add", "201", "--agent", "alpha"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("agent_archived", error.ToString());
        Assert.Contains("archived", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IssueWatchAdd_UnknownAgentOnServer_SurfacesAgentNotFoundCode()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get
                && req.RequestUri?.PathAndQuery == $"/api/projects/{ActiveProjectId}/agents?all=true")
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new[]
                    {
                        new { id = "agent_alpha", name = "alpha" },
                    },
                });
            }
            if (req.Method == HttpMethod.Post)
            {
                return RecordingHttpHandler.Json(
                    new { success = false, error = "Agent 'agent_alpha' was not found in project 'proj_abc'.", code = "agent_not_found" },
                    HttpStatusCode.NotFound);
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "watch", "add", "201", "--agent", "alpha"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("agent_not_found", error.ToString());
    }

    [Fact]
    public async Task IssueWatchList_GetsIssueDetailAndRendersTwoGroups()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get
                && req.RequestUri?.PathAndQuery == $"/api/projects/{ActiveProjectId}/issues/201")
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new
                    {
                        id = "issue_201",
                        number = 201,
                        title = "T",
                        watching = new[]
                        {
                            new { agentId = "agent_alpha", state = "watching", createdAt = "2026-01-01T00:00:00Z", updatedAt = "2026-01-01T00:00:00Z" },
                        },
                        muted = new[]
                        {
                            new { agentId = "agent_beta", state = "muted", createdAt = "2026-01-02T00:00:00Z", updatedAt = "2026-01-02T00:00:00Z" },
                        },
                    },
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "watch", "list", "201"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Single(handler.Requests);
        Assert.Equal($"/api/projects/{ActiveProjectId}/issues/201", handler.Requests[0].RequestUri?.PathAndQuery);

        var stdout = output.ToString();
        Assert.Contains("watching:", stdout, StringComparison.Ordinal);
        Assert.Contains("agent_alpha", stdout, StringComparison.Ordinal);
        Assert.Contains("muted:", stdout, StringComparison.Ordinal);
        Assert.Contains("agent_beta", stdout, StringComparison.Ordinal);
        Assert.Contains("issue:", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IssueWatchList_WithNoEntries_RendersOnlyIssueHeader()
    {
        var (_, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get
                && req.RequestUri?.PathAndQuery == $"/api/projects/{ActiveProjectId}/issues/201")
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new
                    {
                        id = "issue_201",
                        number = 201,
                        title = "T",
                        watching = Array.Empty<object>(),
                        muted = Array.Empty<object>(),
                    },
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "watch", "list", "201"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("issue:", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("watching:", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("muted:", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IssueView_RendersWatchingAndMutedSectionsFromReadModel()
    {
        var (_, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get
                && req.RequestUri?.PathAndQuery == $"/api/projects/{ActiveProjectId}/issues/201")
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new
                    {
                        id = "issue_201",
                        number = 201,
                        title = "T",
                        status = "backlog",
                        priority = "p2",
                        watching = new[]
                        {
                            new { agentId = "agent_alpha", state = "watching", createdAt = "2026-01-01T00:00:00Z", updatedAt = "2026-01-01T00:00:00Z" },
                        },
                        muted = new[]
                        {
                            new { agentId = "agent_beta", state = "muted", createdAt = "2026-01-02T00:00:00Z", updatedAt = "2026-01-02T00:00:00Z" },
                        },
                    },
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "show", "201"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("watching:", stdout, StringComparison.Ordinal);
        Assert.Contains("agent_alpha", stdout, StringComparison.Ordinal);
        Assert.Contains("muted:", stdout, StringComparison.Ordinal);
        Assert.Contains("agent_beta", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IssueWatch_HelpListsAddRemoveAndListSubcommands()
    {
        var (_, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "watch", "--help"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("add", stdout);
        Assert.Contains("remove", stdout);
        Assert.Contains("list", stdout);
    }

    [Fact]
    public async Task IssueWatchAdd_AcceptsProjectIdFlag()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get
                && req.RequestUri?.PathAndQuery == "/api/projects/proj_xyz/agents?all=true")
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new[]
                    {
                        new { id = "agent_alpha", name = "alpha" },
                    },
                });
            }
            if (req.Method == HttpMethod.Post)
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new { id = "issue_201", number = 201, title = "T" },
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["issue", "watch", "add", "201", "--agent", "alpha", "--project", "proj_xyz"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var postReq = handler.Requests.Last(r => r.Method == HttpMethod.Post);
        Assert.Equal("/api/projects/proj_xyz/issues/201/watch", postReq.RequestUri?.PathAndQuery);
    }
}
