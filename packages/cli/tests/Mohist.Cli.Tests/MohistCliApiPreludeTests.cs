using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public class MohistCliApiPreludeTests
{
    private static (MohistCliApi Api, StringWriter Output, StringWriter Error) CreateApi(
        string? activeProjectId = "proj_abc")
    {
        var (_, http, output, error, fs, executor) = CliTestFactory.Create(activeProjectId: activeProjectId);
        var api = new MohistCliApi(http, output, error, fs, executor);
        return (api, output, error);
    }

    private static string CliStatePath() =>
        Path.Combine(CliTestFactory.UserHome, ".mohist", "cli-state.json");

    [Fact]
    public void ResolveOutputMode_Unset_ReturnsTableAndExitZero()
    {
        var (api, _, _) = CreateApi();

        var (mode, exit) = api.ResolveOutputMode(null);

        Assert.Equal("table", mode);
        Assert.Equal(0, exit);
    }

    [Fact]
    public void ResolveOutputMode_Blank_ReturnsTableAndExitZero()
    {
        var (api, _, _) = CreateApi();

        var (mode, exit) = api.ResolveOutputMode("   ");

        Assert.Equal("table", mode);
        Assert.Equal(0, exit);
    }

    [Fact]
    public void ResolveOutputMode_Json_ReturnsDiscoveryAndExitZero()
    {
        var (api, _, _) = CreateApi();

        var (mode, exit) = api.ResolveOutputMode("json");

        Assert.Equal("discover", mode);
        Assert.Equal(0, exit);
    }

    [Fact]
    public void ResolveOutputMode_Table_ReturnsTableAndExitZero()
    {
        var (api, _, _) = CreateApi();

        var (mode, exit) = api.ResolveOutputMode("table");

        Assert.Equal("table", mode);
        Assert.Equal(0, exit);
    }

    [Fact]
    public void ResolveOutputMode_Xml_ReturnsSelectedFieldMode()
    {
        var (api, _, error) = CreateApi();

        var (mode, exit) = api.ResolveOutputMode("xml");

        Assert.Equal("json:xml", mode);
        Assert.Equal(0, exit);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public void ResolveOutputMode_Csv_ReturnsSelectedFieldMode()
    {
        var (api, _, error) = CreateApi();

        var (mode, exit) = api.ResolveOutputMode("csv");

        Assert.Equal("json:csv", mode);
        Assert.Equal(0, exit);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task ResolveProject_ProjectName_ReturnsThatIdAndExitZero()
    {
        var (api, _, _) = CreateApi();

        var (projectId, exit) = await api.ResolveProject("proj_xyz", null);

        Assert.Equal("proj_xyz", projectId);
        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task ResolveProject_ProjectId_ReturnsThatIdAndExitZero()
    {
        var (api, _, _) = CreateApi();

        var (projectId, exit) = await api.ResolveProject(null, "proj_qrs");

        Assert.Equal("proj_qrs", projectId);
        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task ResolveProject_BothMatching_ReturnsThatIdAndExitZero()
    {
        var (api, _, _) = CreateApi();

        var (projectId, exit) = await api.ResolveProject("proj_aaa", "proj_aaa");

        Assert.Equal("proj_aaa", projectId);
        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task ResolveProject_BothConflicting_WritesErrorAndExitsOne()
    {
        var (api, _, error) = CreateApi();

        var (projectId, exit) = await api.ResolveProject("proj_aaa", "proj_bbb");

        Assert.Equal("", projectId);
        Assert.Equal(1, exit);
        Assert.Contains("--project and --project-id resolve to different values", error.ToString());
    }

    [Fact]
    public async Task ResolveProject_NeitherWithActive_ReturnsActiveAndExitZero()
    {
        var (api, _, _) = CreateApi(activeProjectId: "proj_active");

        var (projectId, exit) = await api.ResolveProject(null, null);

        Assert.Equal("proj_active", projectId);
        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task ResolveProject_NeitherNoActive_WritesNoActiveMessageAndExitsOne()
    {
        var (api, _, error) = CreateApi(activeProjectId: null);

        var (projectId, exit) = await api.ResolveProject(null, null);

        Assert.Equal("", projectId);
        Assert.Equal(1, exit);
        Assert.Contains(MohistCliCommands.NoActiveProjectMessage, error.ToString());
    }

    [Fact]
    public async Task ResolveProject_NeitherBlankActive_WritesNoActiveMessageAndExitsOne()
    {
        var (_, http, _, error, fs, executor) = CliTestFactory.Create(activeProjectId: null);
        fs.AddFile(CliStatePath(), "{\"activeProjectId\":\"  \"}");
        var api = new MohistCliApi(http, new StringWriter(), error, fs, executor);

        var (projectId, exit) = await api.ResolveProject(null, null);

        Assert.Equal("", projectId);
        Assert.Equal(1, exit);
        Assert.Contains(MohistCliCommands.NoActiveProjectMessage, error.ToString());
    }

    [Fact]
    public async Task ResolveProject_NeitherUnreadableActive_WritesNoActiveMessageAndExitsOne()
    {
        var (_, http, _, error, fs, executor) = CliTestFactory.Create(activeProjectId: null);
        fs.AddFile(CliStatePath(), "not valid json {{{");
        var api = new MohistCliApi(http, new StringWriter(), error, fs, executor);

        var (projectId, exit) = await api.ResolveProject(null, null);

        Assert.Equal("", projectId);
        Assert.Equal(1, exit);
        Assert.Contains(MohistCliCommands.NoActiveProjectMessage, error.ToString());
    }

}
