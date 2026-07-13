using System.Text.Json.Nodes;
using Mohist.Cli;
using Mohist.Cli.TestSupport;
using Xunit;

namespace Mohist.Cli.UnitTests;

public class IssueReadinessRendererTests
{
    [Fact]
    public async Task RenderIssueList_ShowsDraftReadyAndWaitingStates()
    {
        var data = JsonNode.Parse("""
            [
              { "number": 1, "title": "draft-issue", "workflowStage": "plan", "status": "backlog", "priority": "p2", "isDraft": true, "canStart": false, "blocker": { "kind": "draft" } },
              { "number": 2, "title": "ready-issue", "workflowStage": "plan", "status": "backlog", "priority": "p2", "isDraft": false, "canStart": true, "blocker": null },
              { "number": 3, "title": "waiting-issue", "workflowStage": "plan", "status": "backlog", "priority": "p2", "isDraft": false, "canStart": false, "blocker": { "kind": "waiting-for", "issue": { "number": 99, "title": "Blocker" } } }
            ]
            """);

        var output = await RenderAsync(data, MohistCliApi.TableShape.IssueList);

        Assert.Contains("draft", output);
        Assert.Contains("ready", output);
        Assert.Contains("Waiting for #99", output);
    }

    [Fact]
    public async Task RenderIssueList_DoesNotExposeInternalReadinessFieldNames()
    {
        var data = JsonNode.Parse("""
            [
              { "number": 1, "title": "draft-issue", "workflowStage": "plan", "status": "backlog", "priority": "p2", "isDraft": true, "canStart": false, "blocker": { "kind": "draft" } }
            ]
            """);

        var output = await RenderAsync(data, MohistCliApi.TableShape.IssueList);

        Assert.DoesNotContain("startEligibility", output);
        Assert.DoesNotContain("waitingForDelivery", output);
        Assert.DoesNotContain("Reason", output);
    }

    [Fact]
    public async Task RenderIssueShow_ShowsDraftState()
    {
        var data = JsonNode.Parse("""
            { "number": 1, "title": "draft-issue", "workflowStage": "plan", "status": "backlog", "priority": "p2", "isDraft": true, "canStart": false, "blocker": { "kind": "draft" } }
            """);

        var output = await RenderAsync(data, MohistCliApi.TableShape.IssueShow);

        Assert.Contains("state:", output);
        Assert.Contains("draft", output);
    }

    [Fact]
    public async Task RenderIssueShow_ShowsWaitingReason()
    {
        var data = JsonNode.Parse("""
            { "number": 1, "title": "waiting-issue", "workflowStage": "plan", "status": "backlog", "priority": "p2", "isDraft": false, "canStart": false, "blocker": { "kind": "waiting-for", "issue": { "number": 200, "title": "Blocker" } } }
            """);

        var output = await RenderAsync(data, MohistCliApi.TableShape.IssueShow);

        Assert.Contains("Waiting for #200", output);
        Assert.DoesNotContain("startEligibility", output);
        Assert.DoesNotContain("waitingForDelivery", output);
    }

    [Fact]
    public void ResolveDraftFlagState_HandlesAllCombinations()
    {
        Assert.Equal(MohistCliCommands.DraftFlagState.Conflicting, MohistCliCommands.ResolveDraftFlagState(true, true));
        Assert.Equal(MohistCliCommands.DraftFlagState.Ready, MohistCliCommands.ResolveDraftFlagState(true, false));
        Assert.Equal(MohistCliCommands.DraftFlagState.Draft, MohistCliCommands.ResolveDraftFlagState(false, true));
        Assert.Equal(MohistCliCommands.DraftFlagState.Unspecified, MohistCliCommands.ResolveDraftFlagState(false, false));
    }

    [Fact]
    public void FormatIssueState_UsesApiReadinessFields()
    {
        var draft = JsonNode.Parse("""{ "isDraft": true, "canStart": false, "blocker": { "kind": "draft" } }""");
        Assert.Equal("draft", TableRenderer.FormatIssueState(draft));

        var ready = JsonNode.Parse("""{ "isDraft": false, "canStart": true, "blocker": null }""");
        Assert.Equal("ready", TableRenderer.FormatIssueState(ready));

        var waiting = JsonNode.Parse("""{ "isDraft": false, "canStart": false, "blocker": { "kind": "waiting-for", "issue": { "number": 77 } } }""");
        Assert.Equal("Waiting for #77", TableRenderer.FormatIssueState(waiting));
    }

    private static async Task<string> RenderAsync(JsonNode? data, MohistCliApi.TableShape shape)
    {
        var output = new StringWriter();
        var api = new MohistCliApi(
            new HttpClient(new RejectingHttpHandler()) { BaseAddress = new Uri("http://localhost:3456") },
            output,
            new StringWriter(),
            new FakeFileSystem(),
            new FakeCommandExecutor());

        await api.RenderTableAsync(data, shape);
        return output.ToString();
    }

    private sealed class RejectingHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"Unexpected HTTP request: {request.RequestUri}");
    }
}
