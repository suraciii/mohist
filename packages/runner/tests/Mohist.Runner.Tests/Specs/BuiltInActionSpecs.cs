using Mohist.Runner.Actions;
using Mohist.Runner.Handlers;
using Xunit;

namespace Mohist.Runner.Tests.Specs;

public class BuiltInActionSpecs
{
    [Fact]
    public async Task ArtifactExists_FilePresent_Passes()
    {
        using var temp = new TempDir();
        await File.WriteAllTextAsync(System.IO.Path.Combine(temp.Path, "proposal.md"), "ok");

        var result = await new ArtifactExistsAction().ExecuteAsync(SpecHelpers.Context(temp.Path, "check", "core/artifact-exists", new { path = "proposal.md" }));

        Assert.Equal("success", result.Status);
    }

    [Fact]
    public async Task ArtifactExists_FileMissing_Fails()
    {
        using var temp = new TempDir();

        var result = await new ArtifactExistsAction().ExecuteAsync(SpecHelpers.Context(temp.Path, "check", "core/artifact-exists", new { path = "missing.md" }));

        Assert.Equal("failure", result.Status);
        Assert.Contains("missing.md", result.Message);
    }

    [Fact]
    public async Task Marker_FileContainsExpectedMarker_Passes()
    {
        using var temp = new TempDir();
        await File.WriteAllTextAsync(System.IO.Path.Combine(temp.Path, "review.md"), "<promise>PASS</promise>");

        var result = await new MarkerAction().ExecuteAsync(SpecHelpers.Context(temp.Path, "check", "core/marker", new { path = "review.md", expect = "<promise>PASS</promise>" }));

        Assert.Equal("success", result.Status);
    }

    [Fact]
    public async Task Marker_FileWithoutExpectedMarker_Fails()
    {
        using var temp = new TempDir();
        await File.WriteAllTextAsync(System.IO.Path.Combine(temp.Path, "review.md"), "<promise>FAIL</promise>");

        var result = await new MarkerAction().ExecuteAsync(SpecHelpers.Context(temp.Path, "check", "core/marker", new { path = "review.md", expect = "<promise>PASS</promise>" }));

        Assert.Equal("failure", result.Status);
    }

    [Fact]
    public async Task Script_RunSucceeds_Passes()
    {
        using var temp = new TempDir();

        var result = await new ScriptHandler(SpecHelpers.Logger<ScriptHandler>())
            .ExecuteAsync(SpecHelpers.Context(temp.Path, "check", "core/script", new { run = "true", timeout = 10_000 }));

        Assert.Equal("success", result.Status);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("\"kind\":\"script\"", result.Output);
    }

    [Fact]
    public async Task Script_RunFails_ReturnsCommandOutput()
    {
        using var temp = new TempDir();

        var result = await new ScriptHandler(SpecHelpers.Logger<ScriptHandler>())
            .ExecuteAsync(SpecHelpers.Context(temp.Path, "check", "core/script", new { run = "false", timeout = 10_000 }));

        Assert.Equal("failure", result.Status);
        Assert.NotNull(result.Output);
        Assert.Contains("\"run\":\"false\"", result.Output);
    }
}
