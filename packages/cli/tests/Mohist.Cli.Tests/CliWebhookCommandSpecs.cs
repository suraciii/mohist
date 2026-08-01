using System.Net;
using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public sealed class CliWebhookCommandSpecs
{
    [Fact]
    public async Task SubscriptionHelp_ContainsDeliveredCommands()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exit = await MohistCliCommands.RunAsync(
            http, ["webhook", "subscription", "--help"], output, error, fs, executor);

        Assert.Equal(0, exit);
        foreach (var command in new[] { "create", "list", "view", "edit", "enable", "disable", "delete", "rotate-secret", "failures" })
            Assert.Contains(command, output.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Create_PostsSubscriptionWithoutEchoingSecret()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();
        handler.SetResponder((_, _) => Task.FromResult(RecordingHttpHandler.Json(new
        {
            success = true,
            data = Subscription(),
        }, HttpStatusCode.Created)));

        var exit = await MohistCliCommands.RunAsync(
            http,
            ["webhook", "subscription", "create", "release", "--match", "event.type == \"com.mohist.release\"", "--target-url", "https://hooks.example/release", "--secret", "shared-secret", "--project", "proj_test"],
            output,
            error,
            fs,
            executor);

        Assert.Equal(0, exit);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/projects/proj_test/webhook/subscriptions", request.RequestUri?.PathAndQuery);
        var body = JsonNode.Parse(request.Body!)!.AsObject();
        Assert.Equal("release", body["name"]!.GetValue<string>());
        Assert.Equal("event.type == \"com.mohist.release\"", body["match"]!.GetValue<string>());
        Assert.Equal("https://hooks.example/release", body["targetUrl"]!.GetValue<string>());
        Assert.Equal("shared-secret", body["secret"]!.GetValue<string>());
        Assert.DoesNotContain("shared-secret", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("shared-secret", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_RendersSubscriptionColumns()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();
        handler.SetResponder((_, _) => Task.FromResult(RecordingHttpHandler.Json(new
        {
            success = true,
            data = new[]
            {
                Subscription("whsub_release", "release", "active", true),
                Subscription("whsub_audit", "audit", "disabled", false),
            },
        })));

        var exit = await MohistCliCommands.RunAsync(
            http, ["webhook", "subscription", "list", "--project", "proj_test"], output, error, fs, executor);

        Assert.Equal(0, exit);
        Assert.Equal("/api/projects/proj_test/webhook/subscriptions", Assert.Single(handler.Requests).RequestUri?.PathAndQuery);
        var text = output.ToString();
        Assert.Contains("name", text, StringComparison.Ordinal);
        Assert.Contains("status", text, StringComparison.Ordinal);
        Assert.Contains("target url", text, StringComparison.Ordinal);
        Assert.Contains("has secret", text, StringComparison.Ordinal);
        Assert.Contains("release", text, StringComparison.Ordinal);
        Assert.Contains("whsub_release", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("view", "GET", "")]
    [InlineData("enable", "POST", "/enable")]
    [InlineData("disable", "POST", "/disable")]
    [InlineData("delete", "POST", "/archive")]
    public async Task SubscriptionCommands_TargetExpectedEndpoint(string command, string method, string suffix)
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();
        handler.SetResponder((_, _) => Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = Subscription() })));

        string[] args = command == "delete"
            ? ["webhook", "subscription", command, "whsub_1", "--yes", "--project", "proj_test"]
            : ["webhook", "subscription", command, "whsub_1", "--project", "proj_test"];
        var exit = await MohistCliCommands.RunAsync(
            http, args, output, error, fs, executor);

        Assert.Equal(0, exit);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(method, request.Method.Method);
        Assert.Equal($"/api/projects/proj_test/webhook/subscriptions/whsub_1{suffix}", request.RequestUri?.PathAndQuery);
    }
    [Fact]
    public async Task Edit_PatchesOnlySpecifiedFields()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();
        handler.SetResponder((_, _) => Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = Subscription() })));

        var exit = await MohistCliCommands.RunAsync(
            http, ["webhook", "subscription", "edit", "whsub_1", "--target-url", "https://hooks.example/new", "--project", "proj_test"], output, error, fs, executor);

        Assert.Equal(0, exit);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Patch, request.Method);
        Assert.Equal("/api/projects/proj_test/webhook/subscriptions/whsub_1", request.RequestUri?.PathAndQuery);
        var body = JsonNode.Parse(request.Body!)!.AsObject();
        Assert.Single(body);
        Assert.Equal("https://hooks.example/new", body["targetUrl"]!.GetValue<string>());
    }

    [Fact]
    public async Task Edit_WithoutFields_FailsBeforeRequest()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exit = await MohistCliCommands.RunAsync(
            http, ["webhook", "subscription", "edit", "whsub_1", "--project", "proj_test"], output, error, fs, executor);

        Assert.NotEqual(0, exit);
        Assert.Empty(handler.Requests);
        Assert.Contains("At least one", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RotateSecret_PostsSecretWithoutEchoingIt()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();
        handler.SetResponder((_, _) => Task.FromResult(RecordingHttpHandler.Json(new { success = true })));

        var exit = await MohistCliCommands.RunAsync(
            http, ["webhook", "subscription", "rotate-secret", "whsub_1", "--secret", "replacement-secret", "--project", "proj_test"], output, error, fs, executor);

        Assert.Equal(0, exit);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/projects/proj_test/webhook/subscriptions/whsub_1/rotate-secret", request.RequestUri?.PathAndQuery);
        Assert.Equal("replacement-secret", JsonNode.Parse(request.Body!)!["secret"]!.GetValue<string>());
        Assert.DoesNotContain("replacement-secret", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("replacement-secret", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failures_RendersDeliveryFailureColumns()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();
        handler.SetResponder((_, _) => Task.FromResult(RecordingHttpHandler.Json(new
        {
            success = true,
            data = new[]
            {
                new { occurredAt = "2026-08-01T12:00:00+00:00", eventType = "com.mohist.issue.completed", errorSummary = "HTTP 503" },
            },
        })));

        var exit = await MohistCliCommands.RunAsync(
            http, ["webhook", "subscription", "failures", "--subscription-id", "whsub_1", "--project", "proj_test"], output, error, fs, executor);

        Assert.Equal(0, exit);
        Assert.Equal("/api/projects/proj_test/webhook/subscriptions/whsub_1/failures", Assert.Single(handler.Requests).RequestUri?.PathAndQuery);
        var text = output.ToString();
        Assert.Contains("occurred at", text, StringComparison.Ordinal);
        Assert.Contains("event type", text, StringComparison.Ordinal);
        Assert.Contains("error summary", text, StringComparison.Ordinal);
        Assert.Contains("HTTP 503", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubscriptionCommand_WithoutProject_FailsLocally()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(activeProjectId: null);

        var exit = await MohistCliCommands.RunAsync(
            http, ["webhook", "subscription", "list"], output, error, fs, executor);

        Assert.NotEqual(0, exit);
        Assert.Empty(handler.Requests);
        Assert.Contains(MohistCliCommands.NoActiveProjectMessage, error.ToString(), StringComparison.Ordinal);
    }

    private static object Subscription(
        string id = "whsub_1",
        string name = "release",
        string status = "active",
        bool hasSecret = true) => new
    {
        id,
        projectId = "proj_test",
        name,
        match = "event.type == \"com.mohist.release\"",
        targetUrl = "https://hooks.example/release",
        status,
        hasSecret,
        createdAt = "2026-08-01T12:00:00+00:00",
        updatedAt = "2026-08-01T12:00:00+00:00",
    };
}
