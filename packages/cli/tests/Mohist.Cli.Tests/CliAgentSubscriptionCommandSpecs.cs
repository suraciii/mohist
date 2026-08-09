using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public sealed class CliAgentSubscriptionCommandSpecs
{
    [Fact]
    public async Task List_UsesCanonicalEnvelopeAndDoesNotTreatEmptyAs404()
    {
        var (handler, http, output, error, fs, executor) = Setup();
        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "subscription", "list", "agent_1", "--project", "proj_test"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Equal(HttpMethod.Get, handler.Requests.Last().Method);
        Assert.Equal("/api/projects/proj_test/agents/agent_1/subscriptions", handler.Requests.Last().RequestUri?.PathAndQuery);
        Assert.Contains("No subscriptions", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("not found", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Create_SendsCanonicalPayloadAndIdempotencyKey()
    {
        var (handler, http, output, error, fs, executor) = Setup();
        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["agent", "subscription", "create", "agent_1", "--name", "fallback", "--match", "event.type == \"x\"", "--response-prompt", "inspect", "--idempotency-key", "request-1", "--project", "proj_test"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Last();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("request-1", request.Headers["Idempotency-Key"].Single());
        var body = JsonNode.Parse(request.Body!);
        Assert.Equal("event.type == \"x\"", body?["match"]?.GetValue<string>());
    }

    [Theory]
    [InlineData("--name")]
    [InlineData("--match")]
    [InlineData("--response-prompt")]
    public async Task Create_MissingRequiredOptionFailsBeforeAnyHttpRequest(string missingOption)
    {
        var (handler, http, output, error, fs, executor) = Setup();
        var args = new List<string>
        {
            "agent", "subscription", "create", "agent_1",
            "--name", "fallback",
            "--match", "event.type == \"x\"",
            "--response-prompt", "inspect",
            "--project", "proj_test",
        };
        var missingIndex = args.IndexOf(missingOption);
        args.RemoveRange(missingIndex, 2);

        var exitCode = await MohistCliCommands.RunAsync(
            http, args.ToArray(), output, error, fs, executor);

        Assert.Equal(2, exitCode);
        Assert.Contains($"{missingOption} is required", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Create_RetriesResponseLossWithTheSameGeneratedKeyAndPrintsIt()
    {
        var postAttempts = 0;
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            if (path.EndsWith("/agents/agent_1", StringComparison.Ordinal))
                return Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = new { id = "agent_1", name = "Agent" } }));
            if (request.Method == HttpMethod.Post && path.EndsWith("/subscriptions", StringComparison.Ordinal))
            {
                if (++postAttempts == 1)
                    throw new HttpRequestException("response lost");
                return Task.FromResult(RecordingHttpHandler.Json(
                    new
                    {
                        success = true,
                        data = new
                        {
                            id = "rule_1", projectId = "proj_test", agentId = "agent_1", name = "fallback",
                            match = "event.type == \"x\"", responsePrompt = "inspect", @continue = false,
                            position = 1, status = "active", createdAt = "2026-08-09T00:00:00Z", updatedAt = "2026-08-09T00:00:00Z",
                        },
                    },
                    System.Net.HttpStatusCode.Created));
            }
            return Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = new { } }));
        }, activeProjectId: "proj_test");

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["agent", "subscription", "create", "agent_1", "--name", "fallback", "--match", "event.type == \"x\"", "--response-prompt", "inspect", "--project", "proj_test"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var posts = handler.Requests.Where(request => request.Method == HttpMethod.Post).ToArray();
        Assert.Equal(2, posts.Length);
        var key = posts[0].Headers["Idempotency-Key"].Single();
        Assert.Equal(key, posts[1].Headers["Idempotency-Key"].Single());
        Assert.Contains($"Idempotency-Key: {key}", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Idempotency-Key:", error.ToString(), StringComparison.Ordinal);
    }

    private static (RecordingHttpHandler Handler, HttpClient Http, StringWriter Output, StringWriter Error, FakeFileSystem FileSystem, FakeCommandExecutor Executor) Setup()
    {
        return CliTestFactory.Create((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            if (path.EndsWith("/agents/agent_1", StringComparison.Ordinal))
                return Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = new { id = "agent_1", name = "Agent" } }));
            if (path.EndsWith("/subscriptions", StringComparison.Ordinal))
            {
                object data = request.Method == HttpMethod.Get
                    ? new { subscriptions = Array.Empty<object>(), state = "empty", agentStatus = "active", readiness = "Ready", connection = "no_connection" }
                    : new { id = "rule_1", projectId = "proj_test", agentId = "agent_1", name = "fallback", match = "event.type == \"x\"", responsePrompt = "inspect", @continue = false, position = 1, status = "active", createdAt = "2026-08-09T00:00:00Z", updatedAt = "2026-08-09T00:00:00Z" };
                return Task.FromResult(RecordingHttpHandler.Json(new { success = true, data }, request.Method == HttpMethod.Post ? System.Net.HttpStatusCode.Created : System.Net.HttpStatusCode.OK));
            }
            return Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = new { } }));
        }, activeProjectId: "proj_test");
    }
}
