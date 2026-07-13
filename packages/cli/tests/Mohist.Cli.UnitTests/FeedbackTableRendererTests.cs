using System.Text.Json.Nodes;
using Mohist.Cli;
using Mohist.Cli.TestSupport;
using Xunit;

namespace Mohist.Cli.UnitTests;

public class FeedbackTableRendererTests
{
    [Fact]
    public async Task RenderFeedbackList_TruncatesLongBody()
    {
        var longBody = new string('B', 200);
        var data = JsonNode.Parse($$"""
            [
              { "id": "fb_aaa", "stage": "plan", "status": "open", "createdAt": "2026-06-15T00:00:00Z", "body": "{{longBody}}" }
            ]
            """);

        var output = new StringWriter();
        var api = new MohistCliApi(
            CreateRejectingHttpClient(),
            output,
            new StringWriter(),
            new FakeFileSystem(),
            new FakeCommandExecutor());

        await api.RenderTableAsync(data, MohistCliApi.TableShape.FeedbackList);

        var text = output.ToString();
        Assert.Contains("id", text);
        Assert.Contains("stage", text);
        Assert.Contains("status", text);
        Assert.Contains("createdAt", text);
        Assert.Contains("body", text);
        Assert.Contains("fb_aaa", text);
        Assert.Contains("…", text);
        Assert.DoesNotContain(longBody, text);
    }

    [Fact]
    public async Task RenderFeedbackShow_ListsKeyFieldsAndBody()
    {
        var data = JsonNode.Parse("""
            {
              "id": "fb_123",
              "issueNumber": 42,
              "workflowRunId": "wr_xyz",
              "stage": "plan",
              "status": "open",
              "body": "Please add error handling",
              "createdAt": "2026-06-15T00:00:00Z",
              "resolution": null
            }
            """);

        var output = new StringWriter();
        var api = new MohistCliApi(
            CreateRejectingHttpClient(),
            output,
            new StringWriter(),
            new FakeFileSystem(),
            new FakeCommandExecutor());

        await api.RenderTableAsync(data, MohistCliApi.TableShape.FeedbackShow);

        var text = output.ToString();
        Assert.Contains("id:", text);
        Assert.Contains("fb_123", text);
        Assert.Contains("issue:", text);
        Assert.Contains("workflow run:", text);
        Assert.Contains("wr_xyz", text);
        Assert.Contains("stage:", text);
        Assert.Contains("plan", text);
        Assert.Contains("status:", text);
        Assert.Contains("open", text);
        Assert.Contains("body:", text);
        Assert.Contains("Please add error handling", text);
    }

    private static HttpClient CreateRejectingHttpClient() =>
        new(new RejectingHttpHandler()) { BaseAddress = new Uri("http://localhost:3456") };

    private sealed class RejectingHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"Unexpected HTTP request: {request.RequestUri}");
    }
}
