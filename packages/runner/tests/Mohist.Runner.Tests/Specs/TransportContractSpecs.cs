using System.Net;
using System.Text;
using System.Text.Json;
using Mohist.Runner.Transport;
using Xunit;

namespace Mohist.Runner.Tests.Specs;

public class TransportContractSpecs
{
    [Fact]
    public async Task PollResponse_WithJsonString_ParsesInputs()
    {
        var handler = new FakeHttpHandler(_ => Json(new
        {
            workflowRunId = "wr-1",
            workId = "check-1:abc",
            uses = "core/artifact-exists",
            with = "{\"path\":\"proposal.md\"}",
            variables = "{\"issue\":{\"number\":42}}",
            workType = "check",
            stage = "plan",
            title = "Proposal complete",
            session = new
            {
                id = "session-1",
                projectId = "project",
                issueNumber = 42,
                workflowRunId = "wr-1",
                workId = "check-1:abc",
                stage = "plan",
                title = "Proposal complete"
            }
        }));
        var connection = Connection(handler);

        var work = await connection.PollAsync(CancellationToken.None);

        Assert.NotNull(work);
        Assert.Equal("check", work.WorkType);
        Assert.Equal("plan", work.Stage);
        Assert.Equal("proposal.md", work.With!["path"]!.Value.GetString());
        Assert.Equal(42, work.Variables!["issue"]!.Value.GetProperty("number").GetInt32());
        Assert.NotNull(work.Session);
        Assert.Equal("session-1", work.Session.Id);
    }

    [Fact]
    public async Task PollResponse_NoContent_ReturnsNoWork()
    {
        var connection = Connection(new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent)));

        var work = await connection.PollAsync(CancellationToken.None);

        Assert.Null(work);
    }

    [Fact]
    public async Task Report_SendsStatusMessageOutputAndExitCode()
    {
        string? body = null;
        var handler = new FakeHttpHandler(async request =>
        {
            body = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var connection = Connection(handler);

        await connection.ReportAsync(SpecHelpers.Work("check"), new WorkItemResult("fail", "bad", "{\"x\":1}", 2), CancellationToken.None);

        Assert.NotNull(body);
        using var document = JsonDocument.Parse(body);
        Assert.Equal("fail", document.RootElement.GetProperty("status").GetString());
        Assert.Equal("bad", document.RootElement.GetProperty("message").GetString());
        Assert.Equal(2, document.RootElement.GetProperty("exitCode").GetInt32());
    }

    private static HttpServerConnection Connection(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        return new HttpServerConnection(client, "runner-1", SpecHelpers.Logger<HttpServerConnection>());
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
    };

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
            : this(request => Task.FromResult(handler(request)))
        {
        }

        public FakeHttpHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return _handler(request);
        }
    }
}
