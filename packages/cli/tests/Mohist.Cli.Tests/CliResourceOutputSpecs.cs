using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public sealed class CliResourceOutputSpecs
{
    [Fact]
    public async Task IssueList_BareJsonDiscoversFieldsWithoutProjectOrRequest()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(activeProjectId: null);

        var exit = await MohistCliCommands.RunAsync(
            http, ["issue", "list", "--json"], output, error, fs, executor);

        Assert.Equal(0, exit);
        Assert.Equal(
            ["number", "title", "status", "stage", "priority", "labels"],
            JsonNode.Parse(output.ToString())!.AsArray().Select(x => x!.GetValue<string>()).ToArray());
        Assert.Empty(error.ToString());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task IssueShow_ProjectsSingleResourceWithoutEnvelope()
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
            http, ["issue", "show", "7", "--json", "number,title"], output, error, fs, executor);

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
    public async Task IssueShow_LegacyOutputIsRejectedBeforeRequest()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exit = await MohistCliCommands.RunAsync(
            http, ["issue", "show", "7", "--output", "json"], output, error, fs, executor);

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
            http, ["events", "tail", "--json", "id,type"], output, error, fs, executor);

        Assert.Equal(0, exit);
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal(["id", "type"], JsonNode.Parse(lines[0])!.AsObject().Select(p => p.Key).ToArray());
        Assert.DoesNotContain("[", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(error.ToString());
    }
}
