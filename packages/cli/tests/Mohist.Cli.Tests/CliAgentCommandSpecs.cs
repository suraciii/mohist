using System.Net;
using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public class CliAgentCommandSpecs
{
    [Fact]
    public async Task AgentHelp_ListsSubcommands()
    {
        var handler = new RecordingHttpHandler((_, _) => Task.FromResult(RecordingHttpHandler.Json(new { success = true })));
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await RunAsync(handler, ["agent", "--help"], output, error);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("create", stdout);
        Assert.Contains("list", stdout);
        Assert.Contains("show", stdout);
        Assert.Contains("update", stdout);
        // `archive` is the canonical command, `delete` is a transitional name
        // alias of it — System.CommandLine emits both on the canonical row.
        Assert.Contains("archive, delete <name-or-id>", stdout);
        var subcommandLines = stdout
            .Split('\n')
            .Where(line => line.StartsWith("  ", StringComparison.Ordinal))
            .ToList();
        Assert.DoesNotContain(subcommandLines, line => line.TrimStart().StartsWith("delete ", StringComparison.Ordinal));
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
    public async Task AgentCreate_ResolvesInstructionsFromStdinFlagAndDash()
    {
        var handler = new RecordingHttpHandler((_, _) => Task.FromResult(RecordingHttpHandler.Json(new
        {
            success = true,
            data = Agent("agent_123", "reviewer"),
        }, HttpStatusCode.Created)));
        var fileSystem = FileSystemWithProject();

        var flagExit = await RunAsync(handler, ["agent", "create", "--name", "reviewer", "--instructions-stdin"], fileSystem: fileSystem, standardInput: new StringReader("flag stdin prompt"));
        var stdinExit = await RunAsync(handler, ["agent", "create", "--name", "coder", "--instructions", "-"], fileSystem: fileSystem, standardInput: new StringReader("stdin prompt"));

        Assert.Equal(0, flagExit);
        Assert.Equal(0, stdinExit);
        Assert.Equal("flag stdin prompt", JsonNode.Parse(handler.Requests[0].Body!)!["instructions"]?.GetValue<string>());
        Assert.Equal("stdin prompt", JsonNode.Parse(handler.Requests[1].Body!)!["instructions"]?.GetValue<string>());
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

        Assert.Equal(1, missingExit);
        Assert.Contains("--name is required", missingError.ToString());
        Assert.Empty(missingHandler.Requests);
        Assert.NotEqual(0, conflictExit);
        Assert.Contains("Agent name 'reviewer' is already used", conflictError.ToString());
        Assert.Contains("AGENT_NAME_CONFLICT", conflictError.ToString());
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

        var exitCode = await RunAsync(handler, ["agent", "show", "reviewer", "--output", "table"], output: output, fileSystem: FileSystemWithProject());

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

        var exitCode = await RunAsync(handler, ["agent", "show", "missing"], error: error, fileSystem: FileSystemWithProject());

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
            ["agent", "update", "reviewer", "--name", "reviewer-v2", "--instructions", "new prompt", "--agent-config", "{\"model\":\"zhipu/glm\"}", "--skills", "mohist", "--max-concurrent-runs", "3"],
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
            ["agent", "update", "reviewer", "--clear-description", "--clear-agent-config", "--clear-skills", "--clear-max-concurrent-runs"],
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
            ["agent", "update", "reviewer", setFlag, setValue, clearFlag],
            error: error,
            fileSystem: FileSystemWithProject());

        Assert.Equal(1, exitCode);
        Assert.Contains($"{setFlag} cannot be used with {clearFlag}", error.ToString());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AgentUpdate_ConflictFailsClearly()
    {
        var handler = new RecordingHttpHandler((request, _) => Task.FromResult(request.Method == HttpMethod.Get
            ? RecordingHttpHandler.Json(new { success = true, data = new[] { Agent("agent_123", "reviewer") } })
            : RecordingHttpHandler.JsonError("Agent name 'coder' is already used", "AGENT_NAME_CONFLICT", HttpStatusCode.Conflict)));
        var error = new StringWriter();

        var exitCode = await RunAsync(handler, ["agent", "update", "reviewer", "--name", "coder"], error: error, fileSystem: FileSystemWithProject());

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
    public async Task AgentArchive_DeleteAlias_ProducesIdenticalRequestAndOutput()
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

        var canonicalExit = await MohistCliCommands.RunAsync(
            http, ["agent", "archive", "reviewer", "--project-id", "proj_123"], output, error, fs, executor);
        var canonicalStdout = output.ToString();
        var canonicalStderr = error.ToString();
        var canonicalRequests = handler.Requests.ToList();

        output.GetStringBuilder().Clear();
        error.GetStringBuilder().Clear();

        var aliasExit = await MohistCliCommands.RunAsync(
            http, ["agent", "delete", "reviewer", "--project-id", "proj_123"], output, error, fs, executor);
        var aliasStdout = output.ToString();
        var aliasStderr = error.ToString();
        var aliasRequests = handler.Requests.Skip(canonicalRequests.Count).ToList();

        Assert.Equal(canonicalExit, aliasExit);
        Assert.Equal(canonicalStdout, aliasStdout);
        Assert.Equal(canonicalStderr, aliasStderr);
        Assert.Equal(canonicalRequests.Count, aliasRequests.Count);
        for (var i = 0; i < canonicalRequests.Count; i++)
        {
            Assert.Equal(canonicalRequests[i].Method, aliasRequests[i].Method);
            Assert.Equal(canonicalRequests[i].RequestUri, aliasRequests[i].RequestUri);
        }
    }

    [Fact]
    public async Task AgentArchive_AliasDelete_HonorsProjectIdFlag()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(
            (_, _) => Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = Agent("agent_123", "reviewer", status: "archived"),
            })),
            "proj_default");

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "delete", "agent_123", "--project-id", "proj_other"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Equal("/api/projects/proj_other/agents/agent_123", handler.Requests[0].RequestUri?.PathAndQuery);
        Assert.Contains("Agent reviewer (agent_123) archived", output.ToString());
    }

    [Fact]
    public async Task AgentDelete_ArchivesByResolvedName_LegacyDeleteVerbStillWorks()
    {
        // The transitional `delete` verb still works as a name alias of
        // `archive` — pin this legacy entry point separately so the
        // alias-parity contract in the new specs has a single owner and
        // any future drift in `delete` (e.g. an accidental hard-removal)
        // is caught here.
        var handler = new RecordingHttpHandler((request, _) => Task.FromResult(RecordingHttpHandler.Json(new
        {
            success = true,
            data = request.Method == HttpMethod.Get
                ? new[] { Agent("agent_123", "reviewer") }
                : Agent("agent_123", "reviewer", status: "archived"),
        })));
        var output = new StringWriter();

        var exitCode = await RunAsync(handler, ["agent", "delete", "reviewer"], output: output, fileSystem: FileSystemWithProject());

        Assert.Equal(0, exitCode);
        Assert.Equal(HttpMethod.Delete, handler.Requests[1].Method);
        Assert.Equal("/api/projects/proj_123/agents/agent_123", handler.Requests[1].RequestUri?.PathAndQuery);
        Assert.Contains("Agent reviewer (agent_123) archived", output.ToString());
    }

    [Fact]
    public async Task AgentCommand_ServerUnavailableSurfacesStandardError()
    {
        var handler = new RecordingHttpHandler((_, _) => throw new HttpRequestException("offline"));
        var error = new StringWriter();

        var exitCode = await RunAsync(handler, ["agent", "list"], error: error, fileSystem: FileSystemWithProject());

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Server is not running. Start with: mo server start", error.ToString());
    }

    private static Task<int> RunAsync(
        RecordingHttpHandler handler,
        string[] args,
        StringWriter? output = null,
        StringWriter? error = null,
        FakeFileSystem? fileSystem = null,
        TextReader? standardInput = null)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:3456") };
        return MohistCliCommands.RunAsync(
            http,
            args,
            output ?? new StringWriter(),
            error ?? new StringWriter(),
            fileSystem ?? FileSystemWithProject(),
            new FakeCommandExecutor(),
            standardInput: standardInput);
    }

    private static FakeFileSystem FileSystemWithProject()
    {
        var fileSystem = new FakeFileSystem();
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
