using Mohist.Cli;
using Mohist.Cli.Tests.Compatibility;
using Xunit;

namespace Mohist.Cli.Tests.Issue.Api;

public class BodyInputResolverTests
{
    [Fact]
    public async Task ResolveAsync_InlineBody_ReturnsBodyAsSuccess()
    {
        var files = new FakeFileSystem();
        var stdin = new StringReader(string.Empty);
        var error = new StringWriter();

        var result = await BodyInputResolver.ResolveAsync(
            inlineBody: "x",
            bodyFile: null,
            bodyStdin: false,
            files,
            stdin,
            error);

        var success = Assert.IsType<BodyInputResolver.Result.Success>(result);
        Assert.Equal("x", success.Body);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task ResolveAsync_BodyFile_ReturnsFileContentsAsSuccess()
    {
        var files = new FakeFileSystem();
        files.AddFile("body.md", "# hello\nfrom file\n");
        var stdin = new StringReader(string.Empty);
        var error = new StringWriter();

        var result = await BodyInputResolver.ResolveAsync(
            inlineBody: null,
            bodyFile: "body.md",
            bodyStdin: false,
            files,
            stdin,
            error);

        var success = Assert.IsType<BodyInputResolver.Result.Success>(result);
        Assert.Equal("# hello\nfrom file\n", success.Body);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task ResolveAsync_BodyStdin_ReturnsDrainedStdinAsSuccess()
    {
        var files = new FakeFileSystem();
        var stdin = new StringReader("piped body content");
        var error = new StringWriter();

        var result = await BodyInputResolver.ResolveAsync(
            inlineBody: null,
            bodyFile: null,
            bodyStdin: true,
            files,
            stdin,
            error);

        var success = Assert.IsType<BodyInputResolver.Result.Success>(result);
        Assert.Equal("piped body content", success.Body);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task ResolveAsync_ZeroSources_WritesErrorAndReturnsFailure()
    {
        var files = new FakeFileSystem();
        var stdin = new StringReader(string.Empty);
        var error = new StringWriter();

        var result = await BodyInputResolver.ResolveAsync(
            inlineBody: null,
            bodyFile: null,
            bodyStdin: false,
            files,
            stdin,
            error);

        Assert.IsType<BodyInputResolver.Result.Failure>(result);
        var err = error.ToString();
        Assert.Contains("issue body is required", err);
    }

    [Fact]
    public async Task ResolveAsync_InlineAndFile_WritesMutualExclusionErrorAndReturnsFailure()
    {
        var files = new FakeFileSystem();
        files.AddFile("body.md", "from file");
        var stdin = new StringReader(string.Empty);
        var error = new StringWriter();

        var result = await BodyInputResolver.ResolveAsync(
            inlineBody: "literal",
            bodyFile: "body.md",
            bodyStdin: false,
            files,
            stdin,
            error);

        Assert.IsType<BodyInputResolver.Result.Failure>(result);
        var err = error.ToString();
        Assert.Contains("--body", err);
        Assert.Contains("--body-file", err);
        Assert.Contains("mutually exclusive", err);
    }

    [Fact]
    public async Task ResolveAsync_AllThreeSources_WritesMutualExclusionErrorAndReturnsFailure()
    {
        var files = new FakeFileSystem();
        files.AddFile("body.md", "from file");
        var stdin = new StringReader("piped");
        var error = new StringWriter();

        var result = await BodyInputResolver.ResolveAsync(
            inlineBody: "a",
            bodyFile: "body.md",
            bodyStdin: true,
            files,
            stdin,
            error);

        Assert.IsType<BodyInputResolver.Result.Failure>(result);
        var err = error.ToString();
        Assert.Contains("--body", err);
        Assert.Contains("--body-file", err);
        Assert.Contains("--body-stdin", err);
    }

    [Fact]
    public async Task ResolveAsync_StdinAndFile_WritesMutualExclusionErrorAndReturnsFailure()
    {
        var files = new FakeFileSystem();
        files.AddFile("body.md", "from file");
        var stdin = new StringReader("piped");
        var error = new StringWriter();

        var result = await BodyInputResolver.ResolveAsync(
            inlineBody: null,
            bodyFile: "body.md",
            bodyStdin: true,
            files,
            stdin,
            error);

        Assert.IsType<BodyInputResolver.Result.Failure>(result);
        var err = error.ToString();
        Assert.Contains("--body-file", err);
        Assert.Contains("--body-stdin", err);
    }

    [Fact]
    public async Task ResolveAsync_InlineAndStdin_WritesMutualExclusionErrorAndReturnsFailure()
    {
        var files = new FakeFileSystem();
        var stdin = new StringReader("piped");
        var error = new StringWriter();

        var result = await BodyInputResolver.ResolveAsync(
            inlineBody: "literal",
            bodyFile: null,
            bodyStdin: true,
            files,
            stdin,
            error);

        Assert.IsType<BodyInputResolver.Result.Failure>(result);
        var err = error.ToString();
        Assert.Contains("--body", err);
        Assert.Contains("--body-stdin", err);
    }

    [Fact]
    public async Task ResolveAsync_FileDoesNotExist_WritesFileReadErrorAndReturnsFailure()
    {
        var files = new FakeFileSystem();
        var stdin = new StringReader(string.Empty);
        var error = new StringWriter();

        var result = await BodyInputResolver.ResolveAsync(
            inlineBody: null,
            bodyFile: "missing.md",
            bodyStdin: false,
            files,
            stdin,
            error);

        Assert.IsType<BodyInputResolver.Result.Failure>(result);
        var err = error.ToString();
        Assert.Contains("could not read body file", err);
        Assert.Contains("missing.md", err);
    }

    [Fact]
    public async Task ResolveAsync_StdinDrainedToEndOfStream()
    {
        var files = new FakeFileSystem();
        var stdin = new StringReader("line one\nline two\nline three");
        var error = new StringWriter();

        var result = await BodyInputResolver.ResolveAsync(
            inlineBody: null,
            bodyFile: null,
            bodyStdin: true,
            files,
            stdin,
            error);

        var success = Assert.IsType<BodyInputResolver.Result.Success>(result);
        Assert.Equal("line one\nline two\nline three", success.Body);
        Assert.Equal(-1, stdin.Peek());
    }

    [Fact]
    public async Task ResolveAsync_DoesNotMakeHttpCall_OnAnyPath()
    {
        var files = new FakeFileSystem();
        var stdin = new StringReader(string.Empty);
        var error = new StringWriter();
        var http = new RecordingHttpHandler();

        await BodyInputResolver.ResolveAsync(
            inlineBody: "x",
            bodyFile: null,
            bodyStdin: false,
            files,
            stdin,
            error);
        await BodyInputResolver.ResolveAsync(
            inlineBody: null,
            bodyFile: "body.md",
            bodyStdin: false,
            files,
            stdin,
            error);
        await BodyInputResolver.ResolveAsync(
            inlineBody: null,
            bodyFile: null,
            bodyStdin: true,
            files,
            stdin,
            error);
        await BodyInputResolver.ResolveAsync(
            inlineBody: null,
            bodyFile: null,
            bodyStdin: false,
            files,
            stdin,
            error);
        await BodyInputResolver.ResolveAsync(
            inlineBody: "x",
            bodyFile: "body.md",
            bodyStdin: false,
            files,
            stdin,
            error);

        Assert.Empty(http.Requests);
    }

    private sealed class RecordingHttpHandler : System.Net.Http.HttpMessageHandler
    {
        public List<System.Net.Http.HttpRequestMessage> Requests { get; } = new();

        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{ "success": true, "data": null }"""),
            });
        }
    }
}
