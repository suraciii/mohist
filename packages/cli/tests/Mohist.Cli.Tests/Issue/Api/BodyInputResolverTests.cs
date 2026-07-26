using Mohist.Cli;
using Mohist.Cli.Tests.Compatibility;
using Xunit;

namespace Mohist.Cli.Tests.Issue.Api;

public sealed class BodyInputResolverTests
{
    [Fact]
    public async Task ResolveAsync_InlineBody_ReturnsBodyAsSuccess()
    {
        var result = await ResolveAsync("x", null);

        Assert.Equal("x", Assert.IsType<BodyInputResolver.Result.Success>(result).Body);
    }

    [Fact]
    public async Task ResolveAsync_BodyFile_ReturnsFileContentsAsSuccess()
    {
        var files = new FakeFileSystem();
        files.AddFile("body.md", "# hello\nfrom file\n");

        var result = await ResolveAsync(null, "body.md", files);

        Assert.Equal("# hello\nfrom file\n", Assert.IsType<BodyInputResolver.Result.Success>(result).Body);
    }

    [Fact]
    public async Task ResolveAsync_DashFile_ReadsStandardInput()
    {
        var stdin = new StringReader("piped body content");

        var result = await ResolveAsync(null, "-", standardInput: stdin);

        Assert.Equal("piped body content", Assert.IsType<BodyInputResolver.Result.Success>(result).Body);
        Assert.Equal(-1, stdin.Peek());
    }

    [Fact]
    public async Task ResolveAsync_MissingBody_WritesCanonicalSourceGuidance()
    {
        var error = new StringWriter();

        var result = await BodyInputResolver.ResolveAsync(
            inlineBody: null,
            bodyFile: null,
            new FakeFileSystem(),
            new StringReader(string.Empty),
            error);

        Assert.IsType<BodyInputResolver.Result.Failure>(result);
        Assert.Contains("--body or --body-file", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_InlineAndFile_WritesMutualExclusionError()
    {
        var files = new FakeFileSystem();
        files.AddFile("body.md", "from file");
        var error = new StringWriter();

        var result = await BodyInputResolver.ResolveAsync(
            "literal",
            "body.md",
            files,
            new StringReader(string.Empty),
            error);

        Assert.IsType<BodyInputResolver.Result.Failure>(result);
        Assert.Contains("--body, --body-file", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_MissingFile_WritesFileReadError()
    {
        var error = new StringWriter();

        var result = await BodyInputResolver.ResolveAsync(
            null,
            "missing.md",
            new FakeFileSystem(),
            new StringReader(string.Empty),
            error);

        Assert.IsType<BodyInputResolver.Result.Failure>(result);
        Assert.Contains("could not read body file: missing.md", error.ToString(), StringComparison.Ordinal);
    }

    private static Task<BodyInputResolver.Result> ResolveAsync(
        string? inlineBody,
        string? bodyFile,
        FakeFileSystem? files = null,
        TextReader? standardInput = null) =>
        BodyInputResolver.ResolveAsync(
            inlineBody,
            bodyFile,
            files ?? new FakeFileSystem(),
            standardInput ?? new StringReader(string.Empty),
            TextWriter.Null);
}
