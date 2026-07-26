using System.Net;
using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

// Both this class and CliNotifySetupCommandSpecs mutate the static
// NotifyCommands.ConfigPathOverride (per-test save/restore). xUnit v3
// parallelizes test classes by default, so running them concurrently
// stomps the shared override and intermittently breaks preflight
// assertions that re-read the path at runtime. Pin them to a single
// non-parallel collection per design/testing.md's collection guidance.
[CollectionDefinition("NotifyCommandConfigPath", DisableParallelization = true)]
public sealed class NotifyCommandConfigPathCollectionDefinition
{
}

[Collection("NotifyCommandConfigPath")]
public class CliAgentCommandSpecs
{
    private const string ManagedPresetRoot = "/mohist-tests/user/.mohist/cli/presets";

    private static string SeedManagedPresets(FakeFileSystem fileSystem)
    {
        // The real production path resolves presets from the managed cache at
        // <home>/.mohist/cli/presets (home is /mohist-tests/user for a fake
        // file system). Seeding here exercises PresetCatalog.CreateDefault
        // end-to-end instead of bypassing it via a static override.
        var root = ManagedPresetRoot;
        fileSystem.CreateDirectory(root);
        fileSystem.CreateDirectory($"{root}/supervisor");
        fileSystem.AddFile($"{root}/manifest.json", """
            {
              "supervisor": {
                "instructions": "supervisor/instructions.md",
                "rules": [
                  { "name": "supervisor-approval", "match": "event.type == \"com.mohist.workflow.stage.approval-requested\"", "responsePrompt": "supervisor/approval.md" },
                  { "name": "supervisor-failure", "match": "event.type == \"com.mohist.workflow.run.failed\"", "responsePrompt": "supervisor/failure.md" }
                ]
              }
            }
            """);
        fileSystem.AddFile($"{root}/supervisor/instructions.md", "identity");
        fileSystem.AddFile($"{root}/supervisor/approval.md", "approve response");
        fileSystem.AddFile($"{root}/supervisor/failure.md", "failure response");
        return root;
    }

    private static string SeedManagedPresetsWithPlaceholders(FakeFileSystem fileSystem)
    {
        // Same layout as SeedManagedPresets but with real {{event.*}} prompt
        // text, so the verbatim-preservation assertion runs against content
        // that actually carries runtime placeholders.
        var root = ManagedPresetRoot;
        fileSystem.CreateDirectory(root);
        fileSystem.CreateDirectory($"{root}/supervisor");
        fileSystem.AddFile($"{root}/manifest.json", """
            {
              "supervisor": {
                "instructions": "supervisor/instructions.md",
                "rules": [
                  { "name": "supervisor-approval", "match": "event.type == \"com.mohist.workflow.stage.approval-requested\"", "responsePrompt": "supervisor/approval.md" },
                  { "name": "supervisor-failure", "match": "event.type == \"com.mohist.workflow.run.failed\"", "responsePrompt": "supervisor/failure.md" }
                ]
              }
            }
            """);
        fileSystem.AddFile($"{root}/supervisor/instructions.md", "identity");
        fileSystem.AddFile($"{root}/supervisor/approval.md", "Issue #{{event.issue}} at {{event.stage}} ({{event.workflowrunid}})");
        fileSystem.AddFile($"{root}/supervisor/failure.md", "Run {{event.workflowrunid}} for #{{event.issue}} failed");
        return root;
    }

    [Fact]
    public async Task AgentInstall_UnknownPresetListsAvailableNames()
    {
        var handler = new RecordingHttpHandler((_, _) => Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = Array.Empty<object>() })));
        var error = new StringWriter();
        var fileSystem = FileSystemWithProject();

        var exitCode = await RunAsync(handler, ["agent", "install", "acme"], error: error, fileSystem: fileSystem);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("acme", error.ToString());
        Assert.Contains("supervisor", error.ToString());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AgentInstall_WhenManagedPresetsAbsent_ExitsNonZeroBeforeAnyHttp()
    {
        // The F1 regression: managed skill-data present but presets missing
        // (the post-mo-update steady state before presets were synced). Install
        // must surface a clean unknown-preset error and never reach the server.
        var handler = new RecordingHttpHandler((_, _) => Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = Array.Empty<object>() })));
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:3456") };
        var error = new StringWriter();
        var output = new StringWriter();
        var fileSystem = FileSystemWithProject();
        fileSystem.CreateDirectory("/mohist-tests/user/.mohist/cli/skill-data");

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "install", "supervisor"], output, error, fileSystem, new FakeCommandExecutor());

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Unknown preset 'supervisor'", error.ToString());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AgentInstall_CreatesAgentAndRulesInOrder()
    {
        var fileSystem = FileSystemWithProject();
        var handler = new RecordingHttpHandler((request, _) => Task.FromResult(ResolveRequest(request)));
        var output = new StringWriter();

        var exitCode = await RunAsync(handler, ["agent", "install", "supervisor"], output, fileSystem: fileSystem);

        Assert.Equal(0, exitCode);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal(HttpMethod.Post, handler.Requests[1].Method);
        Assert.Equal(HttpMethod.Get, handler.Requests[2].Method);
        Assert.Equal(HttpMethod.Post, handler.Requests[3].Method);
        Assert.Equal(HttpMethod.Get, handler.Requests[4].Method);
        Assert.Equal(HttpMethod.Post, handler.Requests[5].Method);
        Assert.Equal(HttpMethod.Get, handler.Requests[6].Method);
        Assert.Contains("created agent: supervisor", output.ToString());
        Assert.Contains("created routing rule: supervisor-approval", output.ToString());
        Assert.Contains("created routing rule: supervisor-failure", output.ToString());
        Assert.Null(JsonNode.Parse(handler.Requests[3].Body!)!["continue"]);
        Assert.Null(JsonNode.Parse(handler.Requests[5].Body!)!["continue"]);
        // Rules append at the table tail: the POST bodies carry no before/after
        // anchor, so the server's default tail-append applies (spec: "without
        // specifying a before/after anchor").
        Assert.False(JsonNode.Parse(handler.Requests[3].Body!)!.AsObject().ContainsKey("before"));
        Assert.False(JsonNode.Parse(handler.Requests[3].Body!)!.AsObject().ContainsKey("after"));
        Assert.False(JsonNode.Parse(handler.Requests[5].Body!)!.AsObject().ContainsKey("before"));
        Assert.False(JsonNode.Parse(handler.Requests[5].Body!)!.AsObject().ContainsKey("after"));
        // Both rules must bind to the supervisor Agent — that binding is what
        // actually makes events route to the supervisor (self-review F1).
        var approvalAgentId = JsonNode.Parse(handler.Requests[3].Body!)!["agentId"]?.GetValue<string>();
        var failureAgentId = JsonNode.Parse(handler.Requests[5].Body!)!["agentId"]?.GetValue<string>();
        Assert.Equal("agent_supervisor", approvalAgentId);
        Assert.Equal("agent_supervisor", failureAgentId);
    }

    [Fact]
    public async Task AgentInstall_AgentCreateConflict_ResolvesExistingAndBindsRulesToIt()
    {
        // Concurrent-install race: the list-then-create window is crossed by
        // another install, so POST /agents returns 409 AGENT_NAME_CONFLICT.
        // Install must treat it as "exists, skipped", re-resolve the agent by
        // name against the real project id, and bind both rules to that agent
        // (rather than erroring out with a malformed re-resolve URL).
        var fileSystem = FileSystemWithProject();
        var agentListCalls = 0;
        var handler = new RecordingHttpHandler((request, _) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (request.Method == HttpMethod.Get && path == "/api/projects/proj_123")
            {
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new
                    {
                        id = "proj_123",
                        repositories = new[]
                        {
                            new { name = "default", gitUrl = "https://example.com/repo.git", baseBranch = "main", isDefault = true },
                        },
                    },
                }));
            }

            if (request.Method == HttpMethod.Get && path == "/api/projects/proj_123/agents")
            {
                agentListCalls++;
                // First list (EnsureAgentAsync existence check) sees no agent;
                // the second list (ResolveAgentAsync after the 409) sees the
                // agent the racing install just created. Exact-path match
                // matters: the prior bug built a double-prefixed URL
                // (/api/projects/%2Fapi%2F.../agents) which must NOT match
                // here, so that bug fails this test rather than being hidden.
                var agents = agentListCalls == 1
                    ? Array.Empty<object>()
                    : new[] { Agent("agent_supervisor", "supervisor") };
                return Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = agents }));
            }

            if (request.Method == HttpMethod.Get && path.EndsWith("/routing/rules", StringComparison.Ordinal))
                return Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = Array.Empty<object>() }));

            if (request.Method == HttpMethod.Post && path.EndsWith("/agents", StringComparison.Ordinal))
                return Task.FromResult(RecordingHttpHandler.JsonError("Agent name 'supervisor' is already used", "AGENT_NAME_CONFLICT", HttpStatusCode.Conflict));

            if (request.Method == HttpMethod.Post && path.EndsWith("/routing/rules", StringComparison.Ordinal))
                return Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = new { id = "rule_1", name = "rule" } }, HttpStatusCode.Created));

            return Task.FromResult(RecordingHttpHandler.JsonError("unexpected"));
        });
        var output = new StringWriter();

        var exitCode = await RunAsync(handler, ["agent", "install", "supervisor"], output, fileSystem: fileSystem);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("exists, skipped: agent supervisor", stdout);
        Assert.DoesNotContain("Agent 'supervisor' not found", stdout);
        // Request order: [0]=GET agents (empty), [1]=POST agents (409),
        // [2]=GET agents (re-resolve), [3]=GET rules, [4]=POST approval rule,
        // [5]=GET rules, [6]=POST failure rule.
        // The re-resolve hit the project-scoped agents list, not a malformed
        // double-prefixed URL.
        Assert.Equal("/api/projects/proj_123/agents?all=true", handler.Requests[2].RequestUri?.PathAndQuery);
        // Both rules still bind to the re-resolved supervisor agent.
        var approvalAgentId = JsonNode.Parse(handler.Requests[4].Body!)!["agentId"]?.GetValue<string>();
        var failureAgentId = JsonNode.Parse(handler.Requests[6].Body!)!["agentId"]?.GetValue<string>();
        Assert.Equal("agent_supervisor", approvalAgentId);
        Assert.Equal("agent_supervisor", failureAgentId);
    }

    [Fact]
    public async Task AgentInstall_ResponsePromptPlaceholdersFlowThroughToRuleBodyVerbatim()
    {
        // Pin "stored verbatim, including {{event.*}} placeholders" at the HTTP
        // boundary: the shipped prompt text must reach the rule POST body with
        // its runtime placeholders intact, not sanitized or rendered. Calls
        // MohistCliCommands.RunAsync directly so it can seed placeholder prompt
        // content (the shared RunAsync helper seeds dummy text).
        var fileSystem = FileSystemWithProject();
        SeedManagedPresetsWithPlaceholders(fileSystem);
        var handler = new RecordingHttpHandler((request, _) => Task.FromResult(ResolveRequest(request)));
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:3456") };
        var output = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "install", "supervisor"], output, new StringWriter(), fileSystem, new FakeCommandExecutor());

        Assert.Equal(0, exitCode);
        var approvalPrompt = JsonNode.Parse(handler.Requests[3].Body!)!["responsePrompt"]?.GetValue<string>();
        var failurePrompt = JsonNode.Parse(handler.Requests[5].Body!)!["responsePrompt"]?.GetValue<string>();
        Assert.Contains("{{event.issue}}", approvalPrompt, StringComparison.Ordinal);
        Assert.Contains("{{event.stage}}", approvalPrompt, StringComparison.Ordinal);
        Assert.Contains("{{event.workflowrunid}}", approvalPrompt, StringComparison.Ordinal);
        Assert.Contains("{{event.issue}}", failurePrompt, StringComparison.Ordinal);
        Assert.Contains("{{event.workflowrunid}}", failurePrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentInstall_PartialPreexistence_CreatesOnlyTheMissingRule()
    {
        // Spec: "a project already has an Agent named `supervisor` and a
        // `supervisor-approval` rule but no `supervisor-failure` rule" → install
        // skips the agent and approval rule unmodified and creates only the
        // missing failure rule.
        var fileSystem = FileSystemWithProject();
        var handler = new RecordingHttpHandler((request, _) => Task.FromResult(ResolvePartialPreexistenceRequest(request)));
        var output = new StringWriter();

        var exitCode = await RunAsync(handler, ["agent", "install", "supervisor"], output, fileSystem: fileSystem);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("exists, skipped: agent supervisor", stdout);
        Assert.Contains("exists, skipped: routing rule supervisor-approval", stdout);
        Assert.Contains("created routing rule: supervisor-failure", stdout);
        Assert.DoesNotContain("created agent: supervisor", stdout);
        Assert.DoesNotContain("created routing rule: supervisor-approval", stdout);
        // Exactly one POST: the missing failure rule. The agent and approval
        // rule are reused, not recreated or patched.
        var posts = handler.Requests.Where(r => r.Method == HttpMethod.Post).ToList();
        Assert.Single(posts);
        Assert.Equal("supervisor-failure", JsonNode.Parse(posts[0].Body!)!["name"]?.GetValue<string>());
    }

    [Fact]
    public async Task AgentInstall_RerunSkipsExistingResources()
    {
        var fileSystem = FileSystemWithProject();
        var handler = new RecordingHttpHandler((request, _) => Task.FromResult(ResolveRequest(request, includeExisting: true)));
        var output = new StringWriter();

        var exitCode = await RunAsync(handler, ["agent", "install", "supervisor"], output, fileSystem: fileSystem);

        Assert.Equal(0, exitCode);
        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal(4, output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.DoesNotContain(HttpMethod.Post, handler.Requests.Select(request => request.Method));
    }

    [Fact]
    public async Task AgentInstall_MissingSkillStub_EmitsWarningButInstallsResources()
    {
        var fileSystem = FileSystemWithProject();
        var handler = new RecordingHttpHandler((request, _) => Task.FromResult(ResolveRequest(request)));
        var output = new StringWriter();

        var exitCode = await RunAsync(handler, ["agent", "install", "supervisor"], output, fileSystem: fileSystem);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("created agent: supervisor", stdout);
        Assert.Contains("created routing rule: supervisor-approval", stdout);
        Assert.Contains("created routing rule: supervisor-failure", stdout);
        Assert.Contains("warning: could not find the", stdout);
        Assert.Contains("skill stub", stdout);
        Assert.Contains("local proxy", stdout);
        Assert.Contains("mo skills install --path", stdout);
    }

    [Fact]
    public async Task AgentInstall_SkillStubPresent_NoSkillStubWarning()
    {
        var fileSystem = FileSystemWithProject();
        fileSystem.CreateDirectory("/repo/.agents/skills/mohist");
        var handler = new RecordingHttpHandler((request, _) => Task.FromResult(ResolveRequest(request)));
        var output = new StringWriter();

        var exitCode = await RunAsync(handler, ["agent", "install", "supervisor"], output, fileSystem: fileSystem);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.DoesNotContain("could not find the", stdout);
        Assert.DoesNotContain("mo skills install --path", stdout);
    }

    [Fact]
    public async Task AgentInstall_MissingDefaultRepo_SkipsSkillStubCheckWithNote()
    {
        var fileSystem = FileSystemWithProject();
        var handler = new RecordingHttpHandler((request, _) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (request.Method == HttpMethod.Get && path == "/api/projects/proj_123")
            {
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new { id = "proj_123", repositories = Array.Empty<object>() },
                }));
            }
            return Task.FromResult(ResolveRequest(request));
        });
        var output = new StringWriter();

        var exitCode = await RunAsync(handler, ["agent", "install", "supervisor"], output, fileSystem: fileSystem);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("created agent: supervisor", stdout);
        Assert.Contains("created routing rule: supervisor-approval", stdout);
        Assert.Contains("created routing rule: supervisor-failure", stdout);
        Assert.Contains("note: project has no default repository; skipping skill stub check", stdout);
        Assert.DoesNotContain("could not find the", stdout);
    }

    [Fact]
    public async Task AgentInstall_NotificationsDisabled_EmitsNotificationWarning()
    {
        var fileSystem = FileSystemWithProject();
        var configPath = "/mohist-tests/user/.mohist/config.jsonc";
        fileSystem.AddFile(configPath, """
            {
              "Mohist": {
                "Notifications": {
                  "Hermes": {
                    "EnabledTypes": [ "approval_requested" ]
                  }
                }
              }
            }
            """);
        var previousConfigPath = NotifyCommands.ConfigPathOverride;
        NotifyCommands.ConfigPathOverride = () => configPath;
        try
        {
            var handler = new RecordingHttpHandler((request, _) => Task.FromResult(ResolveRequest(request)));
            var output = new StringWriter();

            var exitCode = await RunAsync(handler, ["agent", "install", "supervisor"], output, fileSystem: fileSystem);

            Assert.Equal(0, exitCode);
            var stdout = output.ToString();
            Assert.Contains("created agent: supervisor", stdout);
            Assert.Contains("warning: Mohist:Notifications:Hermes:EnabledTypes is missing", stdout);
            Assert.Contains("workflow_failed", stdout);
            Assert.Contains("issue_completed", stdout);
        }
        finally
        {
            NotifyCommands.ConfigPathOverride = previousConfigPath;
        }
    }

    [Fact]
    public async Task AgentInstall_NotificationsEnabled_NoNotificationWarning()
    {
        var fileSystem = FileSystemWithProject();
        var configPath = "/mohist-tests/user/.mohist/config.jsonc";
        fileSystem.AddFile(configPath, """
            {
              "Mohist": {
                "Notifications": {
                  "Hermes": {
                    "EnabledTypes": [ "approval_requested", "workflow_failed", "issue_completed" ]
                  }
                }
              }
            }
            """);
        var previousConfigPath = NotifyCommands.ConfigPathOverride;
        NotifyCommands.ConfigPathOverride = () => configPath;
        try
        {
            var handler = new RecordingHttpHandler((request, _) => Task.FromResult(ResolveRequest(request)));
            var output = new StringWriter();

            var exitCode = await RunAsync(handler, ["agent", "install", "supervisor"], output, fileSystem: fileSystem);

            Assert.Equal(0, exitCode);
            var stdout = output.ToString();
            Assert.DoesNotContain("warning: Mohist:Notifications:Hermes:EnabledTypes", stdout);
        }
        finally
        {
            NotifyCommands.ConfigPathOverride = previousConfigPath;
        }
    }

    [Fact]
    public async Task AgentInstall_NoConfigFile_NoNotificationWarning()
    {
        var fileSystem = FileSystemWithProject();
        var configPath = "/mohist-tests/user/.mohist/config.jsonc";
        var previousConfigPath = NotifyCommands.ConfigPathOverride;
        NotifyCommands.ConfigPathOverride = () => configPath;
        try
        {
            var handler = new RecordingHttpHandler((request, _) => Task.FromResult(ResolveRequest(request)));
            var output = new StringWriter();

            var exitCode = await RunAsync(handler, ["agent", "install", "supervisor"], output, fileSystem: fileSystem);

            Assert.Equal(0, exitCode);
            var stdout = output.ToString();
            Assert.DoesNotContain("warning: Mohist:Notifications:Hermes:EnabledTypes", stdout);
        }
        finally
        {
            NotifyCommands.ConfigPathOverride = previousConfigPath;
        }
    }

    [Fact]
    public async Task AgentInstall_ProjectFetchFails_InstallStillSucceeds()
    {
        var fileSystem = FileSystemWithProject();
        var handler = new RecordingHttpHandler((request, _) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (request.Method == HttpMethod.Get && path == "/api/projects/proj_123")
            {
                return Task.FromResult(RecordingHttpHandler.JsonError("Project not found", "project_not_found", HttpStatusCode.NotFound));
            }
            return Task.FromResult(ResolveRequest(request));
        });
        var output = new StringWriter();

        var exitCode = await RunAsync(handler, ["agent", "install", "supervisor"], output, fileSystem: fileSystem);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("created agent: supervisor", stdout);
        Assert.Contains("created routing rule: supervisor-approval", stdout);
        Assert.Contains("created routing rule: supervisor-failure", stdout);
        Assert.Contains("note: project has no default repository; skipping skill stub check", stdout);
    }

    [Fact]
    public async Task AgentInstall_BothChecksFail_EmitsBothWarningsAndInstalls()
    {
        var fileSystem = FileSystemWithProject();
        var configPath = "/mohist-tests/user/.mohist/config.jsonc";
        fileSystem.AddFile(configPath, """
            {
              "Mohist": {
                "Notifications": {
                  "Hermes": {
                    "EnabledTypes": [ "approval_requested" ]
                  }
                }
              }
            }
            """);
        var previousConfigPath = NotifyCommands.ConfigPathOverride;
        NotifyCommands.ConfigPathOverride = () => configPath;
        try
        {
            var handler = new RecordingHttpHandler((request, _) => Task.FromResult(ResolveRequest(request)));
            var output = new StringWriter();

            var exitCode = await RunAsync(handler, ["agent", "install", "supervisor"], output, fileSystem: fileSystem);

            Assert.Equal(0, exitCode);
            var stdout = output.ToString();
            Assert.Contains("created agent: supervisor", stdout);
            Assert.Contains("warning: could not find the", stdout);
            Assert.Contains("skill stub", stdout);
            Assert.Contains("warning: Mohist:Notifications:Hermes:EnabledTypes is missing", stdout);
            Assert.Contains("workflow_failed", stdout);
            Assert.Contains("issue_completed", stdout);
        }
        finally
        {
            NotifyCommands.ConfigPathOverride = previousConfigPath;
        }
    }

    private static HttpResponseMessage ResolveRequest(HttpRequestMessage request, bool includeExisting = false)
    {
        var path = request.RequestUri?.AbsolutePath ?? string.Empty;
        if (request.Method == HttpMethod.Get)
        {
            if (path == "/api/projects/proj_123")
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new
                    {
                        id = "proj_123",
                        repositories = new[]
                        {
                            new { name = "default", gitUrl = "https://example.com/repo.git", baseBranch = "main", isDefault = true },
                        },
                    },
                });
            }

            if (path.EndsWith("/agents", StringComparison.Ordinal))
            {
                var agents = includeExisting ? new[] { Agent("agent_supervisor", "supervisor") } : Array.Empty<object>();
                return RecordingHttpHandler.Json(new { success = true, data = agents });
            }

            if (path.EndsWith("/routing/rules", StringComparison.Ordinal))
            {
                var rules = includeExisting
                    ? new[] { new { id = "rule_1", name = "supervisor-approval" }, new { id = "rule_2", name = "supervisor-failure" } }
                    : Array.Empty<object>();
                return RecordingHttpHandler.Json(new { success = true, data = rules });
            }
        }

        if (request.Method == HttpMethod.Post)
        {
            if (path.EndsWith("/agents", StringComparison.Ordinal))
                return RecordingHttpHandler.Json(new { success = true, data = Agent("agent_supervisor", "supervisor") }, HttpStatusCode.Created);
            if (path.EndsWith("/routing/rules", StringComparison.Ordinal))
                return RecordingHttpHandler.Json(new { success = true, data = new { id = "rule_1", name = "rule" } }, HttpStatusCode.Created);
        }

        return RecordingHttpHandler.JsonError("unexpected");
    }

    private static HttpResponseMessage ResolvePartialPreexistenceRequest(HttpRequestMessage request)
    {
        // Partial pre-existence: the supervisor Agent and the supervisor-approval
        // rule already exist; only supervisor-failure is missing.
        var path = request.RequestUri?.AbsolutePath ?? string.Empty;
        if (request.Method == HttpMethod.Get)
        {
            if (path == "/api/projects/proj_123")
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new
                    {
                        id = "proj_123",
                        repositories = new[]
                        {
                            new { name = "default", gitUrl = "https://example.com/repo.git", baseBranch = "main", isDefault = true },
                        },
                    },
                });
            }

            if (path.EndsWith("/agents", StringComparison.Ordinal))
                return RecordingHttpHandler.Json(new { success = true, data = new[] { Agent("agent_supervisor", "supervisor") } });

            if (path.EndsWith("/routing/rules", StringComparison.Ordinal))
                return RecordingHttpHandler.Json(new { success = true, data = new[] { new { id = "rule_approval", name = "supervisor-approval" } } });
        }

        if (request.Method == HttpMethod.Post && path.EndsWith("/routing/rules", StringComparison.Ordinal))
            return RecordingHttpHandler.Json(new { success = true, data = new { id = "rule_failure", name = "supervisor-failure" } }, HttpStatusCode.Created);

        return RecordingHttpHandler.JsonError("unexpected");
    }

    [Fact]
    public async Task AgentHelp_ListsSubcommands()
    {
        var handler = new RecordingHttpHandler((_, _) => Task.FromResult(RecordingHttpHandler.Json(new { success = true })));
        var output = new StringWriter();
        var error = new StringWriter();
        var exitCode = await RunAsync(handler, ["agent", "--help"], output, error);

        var stdout = output.ToString();
        Assert.Contains("create", stdout);
        Assert.Contains("list", stdout);
        Assert.Contains("view", stdout);
        Assert.Contains("edit", stdout);
        Assert.Contains("archive", stdout);
        Assert.DoesNotContain("show", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("update", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("delete", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("  ls ", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentCreate_SendsRequiredAndOptionalFieldsAndPrintsId()
    {
        var handler = new RecordingHttpHandler((_, _) => Task.FromResult(RecordingHttpHandler.Json(new
        {
            success = true,
            data = Agent("agent_123", "reviewer"),
        }, HttpStatusCode.Created)));
        var output = new StringWriter();
        var error = new StringWriter();
        var fileSystem = FileSystemWithProject();

        var exitCode = await RunAsync(handler,
            ["agent", "create", "--name", "reviewer", "--instructions", "Review strictly", "--description", "Senior reviewer", "--agent-config", "{\"model\":\"openai/gpt-5.5\"}", "--skills", "mohist,fsd", "--max-concurrent-runs", "2"],
            output,
            error,
            fileSystem);

        Assert.Equal(0, exitCode);
        Assert.Equal("agent_123", output.ToString().Trim());
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/projects/proj_123/agents", request.RequestUri?.PathAndQuery);
        var body = JsonNode.Parse(request.Body!)!;
        Assert.Equal("reviewer", body["name"]?.GetValue<string>());
        Assert.Equal("Review strictly", body["instructions"]?.GetValue<string>());
        Assert.Equal("Senior reviewer", body["description"]?.GetValue<string>());
        Assert.Equal("openai/gpt-5.5", body["agentConfig"]?["model"]?.GetValue<string>());
        Assert.Equal("mohist", body["skills"]?[0]?.GetValue<string>());
        Assert.Equal("fsd", body["skills"]?[1]?.GetValue<string>());
        Assert.Equal(2, body["maxConcurrentRuns"]?.GetValue<int>());
    }

    [Fact]
    public async Task AgentCreate_ResolvesInstructionsFromFileDash()
    {
        var handler = new RecordingHttpHandler((_, _) => Task.FromResult(RecordingHttpHandler.Json(new
        {
            success = true,
            data = Agent("agent_123", "reviewer"),
        }, HttpStatusCode.Created)));
        var fileSystem = FileSystemWithProject();

        var stdinExit = await RunAsync(handler, ["agent", "create", "--name", "coder", "--instructions-file", "-"], fileSystem: fileSystem, standardInput: new StringReader("stdin prompt"));

        Assert.Equal(0, stdinExit);
        Assert.Equal("stdin prompt", JsonNode.Parse(handler.Requests[0].Body!)!["instructions"]?.GetValue<string>());
    }

    [Fact]
    public async Task AgentCreate_RejectsInstructionsDashWithoutFileOption()
    {
        var handler = new RecordingHttpHandler((_, _) => throw new InvalidOperationException("API must not be called"));
        var error = new StringWriter();

        var exitCode = await RunAsync(
            handler,
            ["agent", "create", "--name", "coder", "--instructions", "-"],
            error: error,
            fileSystem: FileSystemWithProject());

        Assert.Equal(2, exitCode);
        Assert.Contains("--instructions-file -", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AgentCreate_MissingInstructionsFailsWithScopedUsageBeforeHttp()
    {
        var handler = new RecordingHttpHandler((_, _) => throw new InvalidOperationException("API must not be called"));
        var error = new StringWriter();

        var exitCode = await RunAsync(
            handler,
            ["agent", "create", "--name", "reviewer"],
            error: error,
            fileSystem: FileSystemWithProject());

        Assert.Equal(2, exitCode);
        Assert.Contains("Agent instructions is required", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("Usage:", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("mo agent create [flags]", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AgentCreate_UnreadableInstructionsFileFailsWithScopedUsageBeforeHttp()
    {
        var handler = new RecordingHttpHandler((_, _) => throw new InvalidOperationException("API must not be called"));
        var error = new StringWriter();

        var exitCode = await RunAsync(
            handler,
            ["agent", "create", "--name", "reviewer", "--instructions-file", "missing.md"],
            error: error,
            fileSystem: FileSystemWithProject());

        Assert.Equal(2, exitCode);
        Assert.Contains("could not read body file: missing.md", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("Usage:", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("mo agent create [flags]", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AgentCreate_MissingFieldsAndConflictFailClearly()
    {
        var missingHandler = new RecordingHttpHandler((_, _) => Task.FromResult(RecordingHttpHandler.Json(new { success = true })));
        var missingError = new StringWriter();
        var missingExit = await RunAsync(missingHandler, ["agent", "create"], error: missingError, fileSystem: FileSystemWithProject());

        var conflictHandler = new RecordingHttpHandler((_, _) => Task.FromResult(RecordingHttpHandler.JsonError(
            "Agent name 'reviewer' is already used",
            "AGENT_NAME_CONFLICT",
            HttpStatusCode.Conflict)));
        var conflictError = new StringWriter();
        var conflictExit = await RunAsync(conflictHandler, ["agent", "create", "--name", "reviewer", "--instructions", "prompt"], error: conflictError, fileSystem: FileSystemWithProject());

        Assert.Equal(2, missingExit);
        Assert.Contains("--name is required", missingError.ToString());
        Assert.Contains("Usage:", missingError.ToString(), StringComparison.Ordinal);
        Assert.Contains("mo agent create [flags]", missingError.ToString(), StringComparison.Ordinal);
        Assert.Empty(missingHandler.Requests);
        Assert.NotEqual(0, conflictExit);
        Assert.Contains("Agent name 'reviewer' is already used", conflictError.ToString());
        Assert.Contains("AGENT_NAME_CONFLICT", conflictError.ToString());
    }

    [Fact]
    public async Task AgentLaunch_MissingPromptFailsWithScopedUsageBeforeHttp()
    {
        var handler = new RecordingHttpHandler((_, _) => throw new InvalidOperationException("API must not be called"));
        var error = new StringWriter();

        var exitCode = await RunAsync(
            handler,
            ["agent", "launch", "reviewer"],
            error: error,
            fileSystem: FileSystemWithProject());

        Assert.Equal(2, exitCode);
        Assert.Contains("prompt is required", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("Usage:", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("mo agent launch <agent> [flags]", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AgentUpdate_MissingInstructionsFileFailsWithScopedUsageBeforeHttp()
    {
        var handler = new RecordingHttpHandler((_, _) => throw new InvalidOperationException("API must not be called"));
        var error = new StringWriter();

        var exitCode = await RunAsync(
            handler,
            ["agent", "edit", "reviewer", "--instructions-file", "missing.md"],
            error: error,
            fileSystem: FileSystemWithProject());

        Assert.Equal(2, exitCode);
        Assert.Contains("could not read body file: missing.md", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("Usage:", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("mo agent edit <name-or-id> [flags]", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AgentUpdate_InvalidConfigFailsWithScopedUsageBeforeHttp()
    {
        var handler = new RecordingHttpHandler((_, _) => throw new InvalidOperationException("API must not be called"));
        var error = new StringWriter();

        var exitCode = await RunAsync(
            handler,
            ["agent", "edit", "reviewer", "--agent-config", "not-json"],
            error: error,
            fileSystem: FileSystemWithProject());

        Assert.Equal(2, exitCode);
        Assert.Contains("--agent-config must be valid JSON", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("Usage:", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("mo agent edit <name-or-id> [flags]", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AgentList_UsesDefaultAllAndStatusQueries()
    {
        var handler = new RecordingHttpHandler((_, _) => Task.FromResult(RecordingHttpHandler.Json(new
        {
            success = true,
            data = new[] { Agent("agent_123", "reviewer") },
        })));
        var fileSystem = FileSystemWithProject();

        await RunAsync(handler, ["agent", "list"], fileSystem: fileSystem);
        await RunAsync(handler, ["agent", "list", "--all"], fileSystem: fileSystem);
        await RunAsync(handler, ["agent", "list", "--status", "archived"], fileSystem: fileSystem);

        Assert.Equal("/api/projects/proj_123/agents", handler.Requests[0].RequestUri?.PathAndQuery);
        Assert.Equal("/api/projects/proj_123/agents?all=true", handler.Requests[1].RequestUri?.PathAndQuery);
        Assert.Equal("/api/projects/proj_123/agents?status=archived", handler.Requests[2].RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task AgentShow_ResolvesNameOrIdAndShowsTimestamps()
    {
        var handler = new RecordingHttpHandler((request, _) => Task.FromResult(RecordingHttpHandler.Json(new
        {
            success = true,
            data = request.RequestUri?.PathAndQuery.EndsWith("/agents?all=true") == true
                ? new[] { Agent("agent_123", "reviewer") }
                : Agent("agent_123", "reviewer"),
        })));
        var output = new StringWriter();

        var exitCode = await RunAsync(handler, ["agent", "view", "reviewer",], output: output, fileSystem: FileSystemWithProject());

        Assert.Equal(0, exitCode);
        Assert.Equal("/api/projects/proj_123/agents?all=true", handler.Requests[0].RequestUri?.PathAndQuery);
        Assert.Equal("/api/projects/proj_123/agents/agent_123", handler.Requests[1].RequestUri?.PathAndQuery);
        Assert.Contains("createdAt:", output.ToString());
        Assert.Contains("updatedAt:", output.ToString());
    }

    [Fact]
    public async Task AgentShow_UnknownFailsClearly()
    {
        var handler = new RecordingHttpHandler((_, _) => Task.FromResult(RecordingHttpHandler.Json(new
        {
            success = true,
            data = Array.Empty<object>(),
        })));
        var error = new StringWriter();

        var exitCode = await RunAsync(handler, ["agent", "view", "missing"], error: error, fileSystem: FileSystemWithProject());

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Agent 'missing' not found", error.ToString());
    }

    [Fact]
    public async Task AgentUpdate_ResolvesNameAndSendsMutableFields()
    {
        var handler = new RecordingHttpHandler((request, _) => Task.FromResult(RecordingHttpHandler.Json(new
        {
            success = true,
            data = request.Method == HttpMethod.Get
                ? new[] { Agent("agent_123", "reviewer") }
                : Agent("agent_123", "reviewer-v2", updatedAt: "2026-06-18T02:00:00Z"),
        })));

        var exitCode = await RunAsync(handler,
            ["agent", "edit", "reviewer", "--name", "reviewer-v2", "--instructions", "new prompt", "--agent-config", "{\"model\":\"zhipu/glm\"}", "--skills", "mohist", "--max-concurrent-runs", "3"],
            fileSystem: FileSystemWithProject());

        Assert.Equal(0, exitCode);
        Assert.Equal(HttpMethod.Patch, handler.Requests[1].Method);
        Assert.Equal("/api/projects/proj_123/agents/agent_123", handler.Requests[1].RequestUri?.PathAndQuery);
        var body = JsonNode.Parse(handler.Requests[1].Body!)!;
        Assert.Equal("reviewer-v2", body["name"]?.GetValue<string>());
        Assert.Equal("new prompt", body["instructions"]?.GetValue<string>());
        Assert.Equal("zhipu/glm", body["agentConfig"]?["model"]?.GetValue<string>());
        Assert.Equal("mohist", body["skills"]?[0]?.GetValue<string>());
        Assert.Equal(3, body["maxConcurrentRuns"]?.GetValue<int>());
    }

    [Fact]
    public async Task AgentUpdate_ClearFlagsSendExplicitNulls()
    {
        var handler = new RecordingHttpHandler((request, _) => Task.FromResult(RecordingHttpHandler.Json(new
        {
            success = true,
            data = request.Method == HttpMethod.Get
                ? new[] { Agent("agent_123", "reviewer") }
                : Agent("agent_123", "reviewer", updatedAt: "2026-06-18T02:00:00Z"),
        })));

        var exitCode = await RunAsync(handler,
            ["agent", "edit", "reviewer", "--clear-description", "--clear-agent-config", "--clear-skills", "--clear-max-concurrent-runs"],
            fileSystem: FileSystemWithProject());

        Assert.Equal(0, exitCode);
        var body = JsonNode.Parse(handler.Requests[1].Body!)!.AsObject();
        Assert.True(body.ContainsKey("description"));
        Assert.True(body.ContainsKey("agentConfig"));
        Assert.True(body.ContainsKey("skills"));
        Assert.True(body.ContainsKey("maxConcurrentRuns"));
        Assert.Null(body["description"]);
        Assert.Null(body["agentConfig"]);
        Assert.Null(body["skills"]);
        Assert.Null(body["maxConcurrentRuns"]);
    }

    [Theory]
    [InlineData("--description", "new description", "--clear-description")]
    [InlineData("--agent-config", "{\"model\":\"zhipu/glm\"}", "--clear-agent-config")]
    [InlineData("--skills", "mohist", "--clear-skills")]
    [InlineData("--max-concurrent-runs", "3", "--clear-max-concurrent-runs")]
    public async Task AgentUpdate_ClearFlagsRejectMatchingSetFlags(string setFlag, string setValue, string clearFlag)
    {
        var handler = new RecordingHttpHandler((_, _) => Task.FromResult(RecordingHttpHandler.Json(new { success = true })));
        var error = new StringWriter();

        var exitCode = await RunAsync(handler,
            ["agent", "edit", "reviewer", setFlag, setValue, clearFlag],
            error: error,
            fileSystem: FileSystemWithProject());

        Assert.Equal(2, exitCode);
        Assert.Contains($"{setFlag} cannot be used with {clearFlag}", error.ToString());
        Assert.Contains("Usage:", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("mo agent edit <name-or-id> [flags]", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AgentUpdate_ConflictFailsClearly()
    {
        var handler = new RecordingHttpHandler((request, _) => Task.FromResult(request.Method == HttpMethod.Get
            ? RecordingHttpHandler.Json(new { success = true, data = new[] { Agent("agent_123", "reviewer") } })
            : RecordingHttpHandler.JsonError("Agent name 'coder' is already used", "AGENT_NAME_CONFLICT", HttpStatusCode.Conflict)));
        var error = new StringWriter();

        var exitCode = await RunAsync(handler, ["agent", "edit", "reviewer", "--name", "coder"], error: error, fileSystem: FileSystemWithProject());

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Agent name 'coder' is already used", error.ToString());
        Assert.Contains("AGENT_NAME_CONFLICT", error.ToString());
    }

    [Fact]
    public async Task AgentArchive_ResolvesByIdAndDeletes()
    {
        var handler = new RecordingHttpHandler((_, _) => Task.FromResult(RecordingHttpHandler.Json(new
        {
            success = true,
            data = Agent("agent_123", "reviewer", status: "archived"),
        })));
        var output = new StringWriter();

        var exitCode = await RunAsync(handler, ["agent", "archive", "agent_123"], output: output, fileSystem: FileSystemWithProject());

        Assert.Equal(0, exitCode);
        // Resolving an `agent_` id fetches the agent once (to read the name)
        // and then DELETEs; resolution does not fall through to the list endpoint.
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("/api/projects/proj_123/agents/agent_123", handler.Requests[0].RequestUri?.PathAndQuery);
        Assert.Equal("/api/projects/proj_123/agents/agent_123", handler.Requests[1].RequestUri?.PathAndQuery);
        Assert.Equal(HttpMethod.Delete, handler.Requests[1].Method);
        Assert.Contains("Agent reviewer (agent_123) archived", output.ToString());
    }

    [Fact]
    public async Task AgentArchive_ResolvesByNameAndDeletes()
    {
        var handler = new RecordingHttpHandler((request, _) => Task.FromResult(RecordingHttpHandler.Json(new
        {
            success = true,
            data = request.Method == HttpMethod.Get
                ? new[] { Agent("agent_123", "reviewer") }
                : Agent("agent_123", "reviewer", status: "archived"),
        })));
        var output = new StringWriter();

        var exitCode = await RunAsync(handler, ["agent", "archive", "reviewer"], output: output, fileSystem: FileSystemWithProject());

        Assert.Equal(0, exitCode);
        // Name resolve path: first request is the list lookup, second is the DELETE.
        Assert.Equal("/api/projects/proj_123/agents?all=true", handler.Requests[0].RequestUri?.PathAndQuery);
        Assert.Equal(HttpMethod.Delete, handler.Requests[1].Method);
        Assert.Equal("/api/projects/proj_123/agents/agent_123", handler.Requests[1].RequestUri?.PathAndQuery);
        Assert.Contains("Agent reviewer (agent_123) archived", output.ToString());
    }

    [Fact]
    public async Task AgentArchive_UnresolvedFailsLocallyWithoutHttp()
    {
        var handler = new RecordingHttpHandler((_, _) => Task.FromResult(RecordingHttpHandler.Json(new
        {
            success = true,
            data = Array.Empty<object>(),
        })));
        var error = new StringWriter();

        var exitCode = await RunAsync(handler, ["agent", "archive", "missing"], error: error, fileSystem: FileSystemWithProject());

        Assert.Equal(1, exitCode);
        Assert.Contains("Agent 'missing' not found", error.ToString());
        // Name resolution hits the list once, then fails locally — no DELETE is sent.
        Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
    }

    [Fact]
    public async Task AgentDelete_IsRejectedAsUsageFailure()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(
            (request, _) => Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = request.Method == HttpMethod.Get
                    ? new[] { Agent("agent_123", "reviewer") }
                    : Agent("agent_123", "reviewer", status: "archived"),
            })),
            "proj_123");

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "delete", "reviewer", "--project", "proj_123"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AgentDelete_Directly_IsRejectedAsUsageFailure()
    {
        var handler = new RecordingHttpHandler((request, _) => Task.FromResult(RecordingHttpHandler.Json(new
        {
            success = true,
            data = request.Method == HttpMethod.Get
                ? new[] { Agent("agent_123", "reviewer") }
                : Agent("agent_123", "reviewer", status: "archived"),
        })));
        var output = new StringWriter();

        var exitCode = await RunAsync(handler, ["agent", "delete", "reviewer"], output: output, fileSystem: FileSystemWithProject());

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AgentCommand_ServerUnavailableSurfacesStandardError()
    {
        var handler = new RecordingHttpHandler((_, _) => throw new HttpRequestException("offline"));
        var error = new StringWriter();

        var exitCode = await RunAsync(handler, ["agent", "list"], error: error, fileSystem: FileSystemWithProject());

        Assert.NotEqual(0, exitCode);
        Assert.Contains(MohistCliApi.ServerUnavailableMessage, error.ToString());
    }

    private static Task<int> RunAsync(
        RecordingHttpHandler handler,
        string[] args,
        StringWriter? output = null,
        StringWriter? error = null,
        FakeFileSystem? fileSystem = null,
        TextReader? standardInput = null)
    {
        // `agent install` resolves presets from the managed cache
        // (<home>/.mohist/cli/presets) via the real PresetCatalog.CreateDefault
        // path — seeding here lets every install spec exercise that path
        // without a static override. Harmless for non-install specs, which
        // never read presets.
        var fs = fileSystem ?? FileSystemWithProject();
        SeedManagedPresets(fs);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:3456") };
        return MohistCliCommands.RunAsync(
            http,
            args,
            output ?? new StringWriter(),
            error ?? new StringWriter(),
            fs,
            new FakeCommandExecutor(),
            standardInput: standardInput);
    }

    private static FakeFileSystem FileSystemWithProject(string? currentDirectory = "/repo")
    {
        var fileSystem = new FakeFileSystem
        {
            CurrentDirectory = currentDirectory ?? "/",
        };
        fileSystem.AddFile(
            Path.Combine(CliTestFactory.UserHome, ".mohist", "cli-state.json"),
            "{\"activeProjectId\":\"proj_123\"}");
        return fileSystem;
    }

    private static object Agent(
        string id,
        string name,
        string status = "active",
        string createdAt = "2026-06-18T01:00:00Z",
        string updatedAt = "2026-06-18T01:00:00Z") => new
    {
        id,
        projectId = "proj_123",
        name,
        description = "desc",
        instructions = "prompt",
        agentConfig = new { model = "openai/gpt-5.5" },
        skills = new[] { "mohist" },
        maxConcurrentRuns = 2,
        status,
        createdAt,
        updatedAt,
    };
}
