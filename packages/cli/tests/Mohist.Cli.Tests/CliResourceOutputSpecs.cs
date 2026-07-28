using System.Text.Json.Nodes;
using System.Net;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public sealed class CliResourceOutputSpecs
{
    [Fact]
    public void EveryTableShapeHasAnOutputDescriptor()
    {
        foreach (var shape in Enum.GetValues<MohistCliApi.TableShape>())
        {
            var descriptor = ResourceOutputCatalog.For(shape.ToString());
            Assert.NotEmpty(descriptor.Fields);
        }
    }

    [Fact]
    public void AgentListAndShowUseTheSameFieldCatalog()
    {
        Assert.Same(
            ResourceOutputCatalog.For(nameof(MohistCliApi.TableShape.AgentList)).Fields,
            ResourceOutputCatalog.For(nameof(MohistCliApi.TableShape.AgentShow)).Fields);
        Assert.Same(
            AgentCommands.AgentDescriptor.Fields,
            ResourceOutputCatalog.For(nameof(MohistCliApi.TableShape.AgentShow)).Fields);
        Assert.Equal(
            ResourceOutputCatalog.For(nameof(MohistCliApi.TableShape.AgentShow)).Fields,
            ResourceOutputCatalog.For(nameof(MohistCliApi.TableShape.AgentList)).Fields);
    }

    [Fact]
    public async Task AgentCommands_BareJsonUseOneFieldCatalogWithoutRequest()
    {
        var commands = new[]
        {
            new[] { "agent", "list", "--json" },
            new[] { "agent", "view", "anything", "--json" },
            new[] { "agent", "create", "--json" },
            new[] { "agent", "edit", "anything", "--json" },
            new[] { "agent", "archive", "anything", "--json" },
        };
        var expected = AgentCommands.AgentDescriptor.Fields;

        foreach (var args in commands)
        {
            var (handler, http, output, error, fs, executor) = CliTestFactory.Create(activeProjectId: null);
            var exit = await MohistCliCommands.RunAsync(http, args, output, error, fs, executor);

            Assert.Equal(0, exit);
            Assert.Equal(expected, JsonNode.Parse(output.ToString())!.AsArray().Select(x => x!.GetValue<string>()).ToArray());
            Assert.Empty(error.ToString());
            Assert.Empty(handler.Requests);
        }
    }

    [Fact]
    public void WorkflowPromptAndEpicListCatalogsExposeReadModelFields()
    {
        Assert.Equal(
            ["key", "displayName", "description", "tags", "stage", "body", "source"],
            ResourceOutputCatalog.For(nameof(MohistCliApi.TableShape.WorkflowProfilePrompt)).Fields);
        Assert.Equal(
            ["projectId", "number", "title", "description", "priority", "status", "createdAt", "updatedAt", "progress", "pauseReason"],
            ResourceOutputCatalog.For(nameof(MohistCliApi.TableShape.EpicList)).Fields);
    }

    [Fact]
    public async Task Info_BareJsonDiscoversFieldsWithoutCollectingOrRequesting()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(activeProjectId: null);

        var exit = await MohistCliCommands.RunAsync(
            http, ["info", "--json"], output, error, fs, executor);

        Assert.Equal(0, exit);
        Assert.Equal(
            [
                "cli",
                "server",
                "runner",
                "project",
                "dataDir",
                "platformNotice",
                "skills",
                "gitRemote",
                "opencodeRuntime",
                "envVars",
                "osRuntime",
                "capacity",
                "diskUsage",
            ],
            JsonNode.Parse(output.ToString())!.AsArray().Select(x => x!.GetValue<string>()).ToArray());
        Assert.Empty(error.ToString());
        Assert.Empty(handler.Requests);
        Assert.Empty(executor.Invocations);
    }

    [Fact]
    public async Task IssueList_BareJsonDiscoversFieldsWithoutProjectOrRequest()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(activeProjectId: null);

        var exit = await MohistCliCommands.RunAsync(
            http, ["issue", "list", "--json"], output, error, fs, executor);

        Assert.Equal(0, exit);
        Assert.Equal(
            ["number", "title", "status", "stage", "priority", "risk", "labels", "prereq", "epic", "createdAt", "updatedAt"],
            JsonNode.Parse(output.ToString())!.AsArray().Select(x => x!.GetValue<string>()).ToArray());
        Assert.Empty(error.ToString());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task IssueView_ProjectsSingleResourceWithoutEnvelope()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
            RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    number = 7,
                    title = "Selected",
                    status = "open",
                    body = "not selected",
                },
            }));

        var exit = await MohistCliCommands.RunAsync(
            http, ["issue", "view", "7", "--json", "number,title"], output, error, fs, executor);

        Assert.Equal(0, exit);
        var result = JsonNode.Parse(output.ToString())!.AsObject();
        Assert.Equal(7, result["number"]!.GetValue<int>());
        Assert.Equal("Selected", result["title"]!.GetValue<string>());
        Assert.Null(result["body"]);
        Assert.DoesNotContain("success", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task IssueList_ProjectsCollectionInDescriptorOrder()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
            RecordingHttpHandler.Json(new
            {
                success = true,
                data = new[]
                {
                    new { number = 1, title = "One", status = "open", extra = true },
                    new { number = 2, title = "Two", status = "done", extra = false },
                },
            }));

        var exit = await MohistCliCommands.RunAsync(
            http, ["issue", "list", "--json", "number,title"], output, error, fs, executor);

        Assert.Equal(0, exit);
        var result = JsonNode.Parse(output.ToString())!.AsArray();
        Assert.Equal(2, result.Count);
        Assert.Equal(["number", "title"], result[0]!.AsObject().Select(p => p.Key).ToArray());
        Assert.Equal("One", result[0]! ["title"]!.GetValue<string>());
        Assert.Null(result[0]! ["extra"]);
        Assert.Empty(error.ToString());
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("number,number")]
    [InlineData("number,")]
    public async Task IssueList_InvalidSelectionFailsBeforeProjectOrRequest(string selection)
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(activeProjectId: null);

        var exit = await MohistCliCommands.RunAsync(
            http, ["issue", "list", "--json", selection], output, error, fs, executor);

        Assert.Equal(2, exit);
        Assert.Empty(output.ToString());
        Assert.Contains("bare --json", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task IssueView_LegacyOutputIsRejectedBeforeRequest()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exit = await MohistCliCommands.RunAsync(
            http, ["issue", "view", "7", "--output", "json"], output, error, fs, executor);

        Assert.Equal(2, exit);
        Assert.Empty(output.ToString());
        Assert.Contains("--output", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task EventTail_SelectedFieldsRemainNdjson()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();
        handler.SetResponder((_, _) => Task.FromResult(RecordingHttpHandler.Ndjson([
            "{\"type\":\"one\",\"id\":\"e1\",\"source\":\"test\"}",
            "{\"type\":\"two\",\"id\":\"e2\",\"source\":\"test\"}"]))) ;

        var exit = await MohistCliCommands.RunAsync(
            http, ["event", "tail", "--json", "id,type"], output, error, fs, executor);

        Assert.Equal(0, exit);
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal(["id", "type"], JsonNode.Parse(lines[0])!.AsObject().Select(p => p.Key).ToArray());
        Assert.DoesNotContain("[", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task EpicCreate_SelectedFieldsProjectsPostResult()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(_ =>
            RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { number = 12, title = "Created", status = "open", description = "hidden" },
            }));

        var exit = await MohistCliCommands.RunAsync(
            http, ["epic", "create", "Created", "--project", "proj_test", "--json", "number,title"], output, error, fs, executor);

        Assert.Equal(0, exit);
        Assert.Equal(["number", "title"], JsonNode.Parse(output.ToString())!.AsObject().Select(p => p.Key).ToArray());
        Assert.Empty(error.ToString());
        Assert.Equal(HttpMethod.Post, handler.Requests.Single().Method);
    }

    [Fact]
    public async Task EpicUpdate_DomainFailureKeepsCodeAndDetailsOnStderr()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(_ =>
            RecordingHttpHandler.Json(new
            {
                success = false,
                error = "Epic is already closed",
                code = "epic_terminal",
                details = new { @object = "epic:12", state = "closed", reason = "terminal" },
            }, HttpStatusCode.Conflict));

        var exit = await MohistCliCommands.RunAsync(
            http, ["epic", "edit", "12", "--title", "Changed", "--project", "proj_test", "--json", "number,title"], output, error, fs, executor);

        Assert.Equal(1, exit);
        Assert.Empty(output.ToString());
        Assert.Contains("code=epic_terminal", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("epic:12", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(HttpMethod.Patch, handler.Requests.Single().Method);
    }

    [Fact]
    public async Task EpicUnlink_SelectedFieldsProjectsDeleteResult()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(_ =>
            RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { number = 12, title = "Unlinked", status = "open" },
            }));

        var exit = await MohistCliCommands.RunAsync(
            http, ["epic", "remove", "12", "4", "--project", "proj_test", "--json", "number,title"], output, error, fs, executor);

        Assert.Equal(0, exit);
        Assert.Equal(["number", "title"], JsonNode.Parse(output.ToString())!.AsObject().Select(p => p.Key).ToArray());
        Assert.Equal(HttpMethod.Delete, handler.Requests.Single().Method);
    }

    [Fact]
    public async Task EpicCreate_BareJsonDiscoversFieldsWithoutRequest()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(activeProjectId: null);

        var exit = await MohistCliCommands.RunAsync(
            http, ["epic", "create", "Created", "--json"], output, error, fs, executor);

        Assert.Equal(0, exit);
        Assert.Equal(
            ["projectId", "number", "title", "description", "priority", "status", "createdAt", "updatedAt", "linkedIssues", "progress", "nextIssueNumber", "nextIssueReason", "pauseReason"],
            JsonNode.Parse(output.ToString())!.AsArray().Select(x => x!.GetValue<string>()).ToArray());
        Assert.Empty(handler.Requests);
    }
}
