using System.Net;
using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public class CliAgentSubscriptionCommandSpecs
{
    private const string ActiveProjectId = "proj_test";

    private static (HttpClient http, RecordingHttpHandler handler, StringWriter output, StringWriter error, FakeFileSystem fileSystem, FakeCommandExecutor executor) SetupEnv(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        var (handler, http, output, error, fs, executor) = CliTestHarness.Create(responder, ActiveProjectId);
        return (http, handler, output, error, fs, executor);
    }

    [Fact]
    public async Task AgentHelp_ListsSubscriptionSubcommand()
    {
        var (http, _, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            throw new InvalidOperationException("API must not be called for help"));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "--help"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        Assert.Contains("subscription", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubscriptionHelp_ListsCreateListAndDeleteSubcommands()
    {
        var (http, _, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            throw new InvalidOperationException("API must not be called for help"));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "subscription", "--help"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("create", stdout, StringComparison.Ordinal);
        Assert.Contains("list", stdout, StringComparison.Ordinal);
        Assert.Contains("delete", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubscriptionCreateHelp_ListsResponsePromptInputFlags()
    {
        var (http, _, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            throw new InvalidOperationException("API must not be called for help"));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "subscription", "create", "--help"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("--response-prompt", stdout, StringComparison.Ordinal);
        Assert.Contains("--response-prompt-file", stdout, StringComparison.Ordinal);
        Assert.Contains("--response-prompt-stdin", stdout, StringComparison.Ordinal);
        Assert.Contains("--filter-type", stdout, StringComparison.Ordinal);
        Assert.Contains("--filter-source", stdout, StringComparison.Ordinal);
        Assert.Contains("--filter-subject", stdout, StringComparison.Ordinal);
        Assert.Contains("--priority", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubscriptionCreate_ResolvesAgentById_AndSendsRequiredFields()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            if (path.EndsWith("/agents/agent_123", StringComparison.Ordinal))
            {
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new { id = "agent_123", name = "reviewer", status = "active" },
                }));
            }
            return Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = Subscription("subs_aaa", "reviewer", "com.mohist.workflow.stage.*"),
            }, HttpStatusCode.Created));
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["agent", "subscription", "create", "agent_123",
             "--name", "approve-on-stage",
             "--filter-type", "com.mohist.workflow.stage.*",
             "--response-prompt", "Approve the request."],
            output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("/api/projects/proj_test/agents/agent_123", handler.Requests[0].RequestUri?.PathAndQuery);
        Assert.Equal(HttpMethod.Post, handler.Requests[1].Method);
        Assert.Equal("/api/projects/proj_test/agents/agent_123/subscriptions", handler.Requests[1].RequestUri?.PathAndQuery);

        var body = JsonNode.Parse(handler.Requests[1].Body!)!.AsObject();
        Assert.Equal("approve-on-stage", body["name"]?.GetValue<string>());
        Assert.Equal("com.mohist.workflow.stage.*", body["filter"]?["type"]?.GetValue<string>());
        Assert.False(body["filter"]?.AsObject().ContainsKey("source"));
        Assert.False(body["filter"]?.AsObject().ContainsKey("subject"));
        Assert.Equal("Approve the request.", body["responsePrompt"]?.GetValue<string>());
        Assert.False(body.ContainsKey("priority"));

        var stdout = output.ToString();
        Assert.Contains("subs_aaa", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubscriptionCreate_ResolvesAgentByName_AndSendsFilterConstraintsAndPriority()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            if (path.EndsWith("/agents?all=true", StringComparison.Ordinal))
            {
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new[] { new { id = "agent_xyz", name = "coder", status = "active" } },
                }));
            }
            return Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = Subscription("subs_xyz", "coder", "com.mohist.workflow.stage.approval-requested",
                    source: "/mohist/workflow-runs/wr_42", subject: "issue-391", priority: 7),
            }, HttpStatusCode.Created));
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["agent", "subscription", "create", "coder",
             "--name", "code-review-on-approval",
             "--filter-type", "com.mohist.workflow.stage.approval-requested",
             "--filter-source", "/mohist/workflow-runs/wr_42",
             "--filter-subject", "issue-391",
             "--response-prompt", "Review and approve.",
             "--priority", "7"],
            output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        Assert.True(string.IsNullOrEmpty(error.ToString()), $"unexpected error: {error}");
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("/api/projects/proj_test/agents?all=true", handler.Requests[0].RequestUri?.PathAndQuery);
        var body = JsonNode.Parse(handler.Requests[1].Body!)!.AsObject();
        Assert.Equal("code-review-on-approval", body["name"]?.GetValue<string>());
        Assert.Equal("com.mohist.workflow.stage.approval-requested", body["filter"]?["type"]?.GetValue<string>());
        Assert.Equal("/mohist/workflow-runs/wr_42", body["filter"]?["source"]?.GetValue<string>());
        Assert.Equal("issue-391", body["filter"]?["subject"]?.GetValue<string>());
        Assert.Equal("Review and approve.", body["responsePrompt"]?.GetValue<string>());
        Assert.Equal(7, body["priority"]?.GetValue<int>());
    }

    [Fact]
    public async Task SubscriptionCreate_ReadsResponsePromptFromFile()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            if (path.EndsWith("/agents/agent_123", StringComparison.Ordinal))
            {
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new { id = "agent_123", name = "reviewer", status = "active" },
                }));
            }
            return Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = Subscription("subs_file", "reviewer", "com.mohist.workflow.stage.approval-requested"),
            }, HttpStatusCode.Created));
        });

        fileSystem.AddFile("/tmp/prompt.txt", "Prompt from file body.");

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["agent", "subscription", "create", "agent_123",
             "--name", "from-file",
             "--filter-type", "com.mohist.workflow.stage.approval-requested",
             "--response-prompt-file", "/tmp/prompt.txt"],
            output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var body = JsonNode.Parse(handler.Requests[1].Body!)!.AsObject();
        Assert.Equal("Prompt from file body.", body["responsePrompt"]?.GetValue<string>());
    }

    [Fact]
    public async Task SubscriptionCreate_ReadsResponsePromptFromStdin()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            if (path.EndsWith("/agents/agent_123", StringComparison.Ordinal))
            {
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new { id = "agent_123", name = "reviewer", status = "active" },
                }));
            }
            return Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = Subscription("subs_stdin", "reviewer", "com.mohist.workflow.stage.approval-requested"),
            }, HttpStatusCode.Created));
        });

        var stdin = new StringReader("Prompt piped through stdin.");

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["agent", "subscription", "create", "agent_123",
             "--name", "from-stdin",
             "--filter-type", "com.mohist.workflow.stage.approval-requested",
             "--response-prompt-stdin"],
            output, error, fileSystem, executor,
            standardInput: stdin);

        Assert.Equal(0, exitCode);
        var body = JsonNode.Parse(handler.Requests[1].Body!)!.AsObject();
        Assert.Equal("Prompt piped through stdin.", body["responsePrompt"]?.GetValue<string>());
    }

    [Fact]
    public async Task SubscriptionCreate_MissingRequiredFieldsFailClearly()
    {
        var (http, handler, _, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = new { } })));

        var missingNameExit = await MohistCliCommands.RunAsync(
            http,
            ["agent", "subscription", "create", "agent_123",
             "--filter-type", "com.mohist.workflow.stage.*",
             "--response-prompt", "Hello"],
            new StringWriter(), error, fileSystem, executor);
        Assert.Equal(1, missingNameExit);
        Assert.Contains("--name is required", error.ToString(), StringComparison.Ordinal);

        error.GetStringBuilder().Clear();

        var missingTypeExit = await MohistCliCommands.RunAsync(
            http,
            ["agent", "subscription", "create", "agent_123",
             "--name", "x",
             "--response-prompt", "Hello"],
            new StringWriter(), error, fileSystem, executor);
        Assert.Equal(1, missingTypeExit);
        Assert.Contains("--filter-type is required", error.ToString(), StringComparison.Ordinal);

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SubscriptionCreate_MissingResponsePromptFailsClearly()
    {
        var (http, handler, _, error, fileSystem, executor) = SetupEnv((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            if (path.EndsWith("/agents/agent_123", StringComparison.Ordinal))
            {
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new { id = "agent_123", name = "reviewer", status = "active" },
                }));
            }
            throw new InvalidOperationException("API must not be called when response prompt missing");
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["agent", "subscription", "create", "agent_123",
             "--name", "x",
             "--filter-type", "com.mohist.workflow.stage.*"],
            new StringWriter(), error, fileSystem, executor);

        Assert.Equal(1, exitCode);
        Assert.Contains("response prompt is required", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SubscriptionCreate_UnknownAgentFailsClearly()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            if (path.EndsWith("/agents?all=true", StringComparison.Ordinal))
            {
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = Array.Empty<object>(),
                }));
            }
            throw new InvalidOperationException("API must not be called after agent resolution fails");
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["agent", "subscription", "create", "missing",
             "--name", "x",
             "--filter-type", "com.mohist.workflow.stage.*",
             "--response-prompt", "Hello"],
            output, error, fileSystem, executor);

        Assert.Equal(1, exitCode);
        Assert.Contains("Agent 'missing' not found", error.ToString(), StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task SubscriptionCreate_ArchivedAgentConflictFailsClearly()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            if (path.EndsWith("/agents/agent_123", StringComparison.Ordinal))
            {
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new { id = "agent_123", name = "reviewer", status = "active" },
                }));
            }
            return Task.FromResult(RecordingHttpHandler.JsonError(
                "Archived agents cannot receive new subscriptions",
                "agent_archived",
                HttpStatusCode.Conflict));
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["agent", "subscription", "create", "agent_123",
             "--name", "x",
             "--filter-type", "com.mohist.workflow.stage.*",
             "--response-prompt", "Hello"],
            output, error, fileSystem, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Archived agents cannot receive new subscriptions", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("agent_archived", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubscriptionList_ResolvesAgentAndEnumeratesSubscriptionsTable()
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
                data = new object[]
                {
                    new
                    {
                        id = "subs_aaa",
                        name = "approve-on-stage",
                        filter = new { type = "com.mohist.workflow.stage.*", source = (string?)null, subject = (string?)null },
                        priority = (int?)null,
                        status = "active",
                    },
                    new
                    {
                        id = "subs_bbb",
                        name = "review-on-approval",
                        filter = new { type = "com.mohist.workflow.stage.approval-requested", source = "/mohist/workflow-runs/wr_42", subject = "issue-391" },
                        priority = 7,
                        status = "active",
                    },
                    new
                    {
                        id = "subs_ccc",
                        name = "archived-subscription",
                        filter = new { type = "com.mohist.issue.*", source = (string?)null, subject = (string?)null },
                        priority = (int?)0,
                        status = "archived",
                    },
                },
            }));
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["agent", "subscription", "list", "reviewer"],
            output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        Assert.Equal("/api/projects/proj_test/agents?all=true", handler.Requests[0].RequestUri?.PathAndQuery);
        Assert.Equal("/api/projects/proj_test/agents/agent_123/subscriptions", handler.Requests[1].RequestUri?.PathAndQuery);

        var stdout = output.ToString();
        Assert.Contains("approve-on-stage", stdout, StringComparison.Ordinal);
        Assert.Contains("review-on-approval", stdout, StringComparison.Ordinal);
        Assert.Contains("archived-subscription", stdout, StringComparison.Ordinal);
        Assert.Contains("default", stdout, StringComparison.Ordinal);
        Assert.Contains("active", stdout, StringComparison.Ordinal);
        Assert.Contains("archived", stdout, StringComparison.Ordinal);
        Assert.Contains("com.mohist.workflow.stage.*", stdout, StringComparison.Ordinal);
        Assert.Contains("com.mohist.workflow.stage.approval-requested", stdout, StringComparison.Ordinal);
        Assert.Contains("com.mohist.issue.*", stdout, StringComparison.Ordinal);
        // List view shows the type matcher only; full source/subject constraints
        // are surfaced in `show` (and via `--output json`).
        Assert.DoesNotContain("source=/mohist/workflow-runs/wr_42", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubscriptionList_EmptyListPrintsNoSubscriptions()
    {
        var (http, _, output, error, fileSystem, executor) = SetupEnv((request, _) =>
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
                data = Array.Empty<object>(),
            }));
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["agent", "subscription", "list", "reviewer"],
            output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        Assert.Contains("No subscriptions", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubscriptionList_UnknownAgentFailsClearly()
    {
        var (http, _, output, error, fileSystem, executor) = SetupEnv((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            if (path.EndsWith("/agents?all=true", StringComparison.Ordinal))
            {
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = Array.Empty<object>(),
                }));
            }
            throw new InvalidOperationException("API must not be called after agent resolution fails");
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["agent", "subscription", "list", "missing"],
            output, error, fileSystem, executor);

        Assert.Equal(1, exitCode);
        Assert.Contains("Agent 'missing' not found", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubscriptionDelete_ResolvesAgentAndSendsDeleteRequest()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            if (path.EndsWith("/agents/agent_123", StringComparison.Ordinal))
            {
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new { id = "agent_123", name = "reviewer", status = "active" },
                }));
            }
            return Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { },
            }));
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["agent", "subscription", "delete", "agent_123", "subs_aaa"],
            output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(HttpMethod.Delete, handler.Requests[1].Method);
        Assert.Equal("/api/projects/proj_test/agents/agent_123/subscriptions/subs_aaa", handler.Requests[1].RequestUri?.PathAndQuery);
        Assert.Contains("Subscription subs_aaa deleted", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubscriptionDelete_RmAliasProducesIdenticalRequest()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            if (path.EndsWith("/agents/agent_123", StringComparison.Ordinal))
            {
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new { id = "agent_123", name = "reviewer", status = "active" },
                }));
            }
            return Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { },
            }));
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["agent", "subscription", "rm", "agent_123", "subs_aaa"],
            output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        Assert.Equal(HttpMethod.Delete, handler.Requests[1].Method);
        Assert.Equal("/api/projects/proj_test/agents/agent_123/subscriptions/subs_aaa", handler.Requests[1].RequestUri?.PathAndQuery);
        Assert.Contains("Subscription subs_aaa deleted", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubscriptionDelete_NotFoundFailsClearly()
    {
        var (http, _, output, error, fileSystem, executor) = SetupEnv((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            if (path.EndsWith("/agents/agent_123", StringComparison.Ordinal))
            {
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new { id = "agent_123", name = "reviewer", status = "active" },
                }));
            }
            return Task.FromResult(RecordingHttpHandler.JsonError(
                "Subscription 'subs_zzz' not found",
                null,
                HttpStatusCode.NotFound));
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["agent", "subscription", "delete", "agent_123", "subs_zzz"],
            output, error, fileSystem, executor);

        Assert.Equal(4, exitCode);
        Assert.Contains("Subscription 'subs_zzz' not found", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubscriptionDelete_UnknownAgentFailsClearly()
    {
        var (http, _, output, error, fileSystem, executor) = SetupEnv((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            if (path.EndsWith("/agents?all=true", StringComparison.Ordinal))
            {
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = Array.Empty<object>(),
                }));
            }
            throw new InvalidOperationException("API must not be called after agent resolution fails");
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["agent", "subscription", "delete", "missing", "subs_aaa"],
            output, error, fileSystem, executor);

        Assert.Equal(1, exitCode);
        Assert.Contains("Agent 'missing' not found", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubscriptionCreate_RespectsProjectIdFlag()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            if (path.EndsWith("/agents/agent_123", StringComparison.Ordinal))
            {
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new { id = "agent_123", name = "reviewer", status = "active" },
                }));
            }
            return Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = Subscription("subs_p", "reviewer", "com.mohist.workflow.stage.*"),
            }, HttpStatusCode.Created));
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["agent", "subscription", "create", "agent_123",
             "--name", "x",
             "--filter-type", "com.mohist.workflow.stage.*",
             "--response-prompt", "Hello",
             "--project-id", "proj_other"],
            output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        Assert.Equal("/api/projects/proj_other/agents/agent_123", handler.Requests[0].RequestUri?.PathAndQuery);
        Assert.Equal("/api/projects/proj_other/agents/agent_123/subscriptions", handler.Requests[1].RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task SubscriptionCreate_JsonOutputPrintsRawServerPayload()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            if (path.EndsWith("/agents/agent_123", StringComparison.Ordinal))
            {
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new { id = "agent_123", name = "reviewer", status = "active" },
                }));
            }
            return Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = Subscription("subs_json", "reviewer", "com.mohist.workflow.stage.*"),
            }, HttpStatusCode.Created));
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["agent", "subscription", "create", "agent_123",
             "--name", "x",
             "--filter-type", "com.mohist.workflow.stage.*",
             "--response-prompt", "Hello",
             "--output", "json"],
            output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        Assert.Contains("subs_json", output.ToString(), StringComparison.Ordinal);
        // JSON mode must print the raw server envelope (success=true, data={...}),
        // not the rendered show table — pin a discriminator field so the contract is
        // obvious if a future refactor accidentally renders a table in json mode.
        Assert.Contains("\"success\": true", output.ToString(), StringComparison.Ordinal);
    }

    private static object Subscription(
        string id,
        string agentName,
        string filterType,
        string? source = null,
        string? subject = null,
        int? priority = null) => new
        {
            id,
            projectId = ActiveProjectId,
            agentId = "agent_123",
            agentName,
            name = "x",
            filter = new { type = filterType, source, subject },
            responsePrompt = "Rendered prompt.",
            priority,
            status = "active",
            createdAt = "2026-07-06T00:00:00Z",
            updatedAt = "2026-07-06T00:00:00Z",
        };
}