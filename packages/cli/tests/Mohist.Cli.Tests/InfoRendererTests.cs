using System.Text.Json.Nodes;
using Xunit;

namespace Mohist.Cli.Tests;

public class InfoRendererTests
{
    private static InfoResult BuildSampleResult(bool includeVerbose = false)
    {
        InfoVerbose? verbose = null;
        if (includeVerbose)
        {
            verbose = new InfoVerbose(
                Skills: new InfoVerboseSkills([], Resolved: true),
                GitRemote: new InfoVerboseGitRemote("https://example.test/repo.git", IsGitRepo: true),
                OpencodeRuntime: new InfoVerboseOpencodeRuntime("opencode", "1.0.0", 5, Resolved: true),
                EnvVars: [new InfoVerboseEnvVar("RUNNER_ID", "r1")],
                OsRuntime: new InfoVerboseOsRuntime("linux", "x64", ".NET 11.0", "v22.5.0"),
                Capacity: new InfoVerboseCapacity(1),
                DiskUsage: new InfoVerboseDiskUsage([], Resolved: true));
        }

        return new InfoResult(
            Cli: new InfoCli("1.0.0", "/usr/bin/mo"),
            Server: new InfoService(
                new InfoServiceStatus("active", 1234, "5m"),
                new InfoSource("/repo", "a1b2c3d", "Add info")),
            Runner: new InfoService(
                new InfoServiceStatus("active", 5678, "3m"),
                new InfoSource("/repo", "a1b2c3d", "Add info")),
            Project: new InfoProject("proj_1", "mohist-local", 96, 22),
            DataDir: new InfoDataDir("/home/.mohist", "412 MB"),
            PlatformNotice: null,
            Verbose: verbose);
    }

    [Fact]
    public void RenderDefault_ProducesAllExpectedSections()
    {
        var result = BuildSampleResult();
        var renderer = new InfoRenderer();

        var writer = new StringWriter();
        renderer.RenderDefault(writer, result);

        var text = writer.ToString();
        Assert.Contains("CLI", text);
        Assert.Contains("Server", text);
        Assert.Contains("Runner", text);
        Assert.Contains("Project", text);
        Assert.Contains("Data dir", text);
        Assert.Contains("a1b2c3d", text);
        Assert.Contains("mohist-local", text);
        Assert.Contains("412 MB", text);
    }

    [Fact]
    public void RenderDefault_ShowsActiveStateWithPidAndUptime()
    {
        var result = BuildSampleResult();
        var renderer = new InfoRenderer();

        var writer = new StringWriter();
        renderer.RenderDefault(writer, result);

        var text = writer.ToString();
        Assert.Contains("active", text);
        Assert.Contains("1234", text);
        Assert.Contains("5m", text);
    }

    [Fact]
    public void RenderJson_Default_ProducesValidSingleLineJson()
    {
        var result = BuildSampleResult();
        var renderer = new InfoRenderer();

        var writer = new StringWriter();
        renderer.RenderJson(writer, result);

        var node = JsonNode.Parse(writer.ToString()) as JsonObject;
        Assert.NotNull(node);
        var keys = node!.Select(kv => kv.Key).ToHashSet();
        Assert.Contains("cli", keys);
        Assert.Contains("server", keys);
        Assert.Contains("runner", keys);
        Assert.Contains("project", keys);
        Assert.Contains("dataDir", keys);
        Assert.Contains("platformNotice", keys);
    }

    [Fact]
    public void RenderJson_Verbose_AddsAllVerboseSections()
    {
        var result = BuildSampleResult(includeVerbose: true);
        var renderer = new InfoRenderer();

        var writer = new StringWriter();
        renderer.RenderJson(writer, result);

        var node = JsonNode.Parse(writer.ToString()) as JsonObject;
        Assert.NotNull(node);
        var keys = node!.Select(kv => kv.Key).ToHashSet();
        Assert.Contains("skills", keys);
        Assert.Contains("gitRemote", keys);
        Assert.Contains("opencodeRuntime", keys);
        Assert.Contains("envVars", keys);
        Assert.Contains("osRuntime", keys);
        Assert.Contains("capacity", keys);
        Assert.Contains("diskUsage", keys);
    }

    [Fact]
    public void BuildCliLine_PrefixesVersionWithV()
    {
        var line = InfoRenderer.BuildCliLine(new InfoCli("1.0.0", "/usr/bin/mo", null));
        Assert.Contains("v1.0.0", line);
    }

    [Fact]
    public void BuildCliLine_AppendsBuildDateWhenPresent()
    {
        var line = InfoRenderer.BuildCliLine(new InfoCli("1.0.0", "/usr/bin/mo", "2026-06-14"));
        Assert.Contains("(built 2026-06-14)", line);
    }

    [Fact]
    public void BuildServiceLine_ActiveState_IncludesPidAndUptime()
    {
        var line = InfoRenderer.BuildServiceLine("Server",
            new InfoService(new InfoServiceStatus("active", 1234, "5m"), null),
            includeSource: true);
        Assert.Contains("active", line);
        Assert.Contains("1234", line);
        Assert.Contains("5m", line);
    }

    [Fact]
    public void BuildJsonObject_AllSectionsHaveExpectedFields()
    {
        var result = BuildSampleResult();
        var root = InfoRenderer.BuildJsonObject(result);

        var cli = root["cli"] as JsonObject;
        Assert.Equal("1.0.0", (string?)cli!["version"]);
        Assert.Equal("/usr/bin/mo", (string?)cli["binaryPath"]);

        var project = root["project"] as JsonObject;
        Assert.Equal("proj_1", (string?)project!["id"]);
        Assert.Equal("mohist-local", (string?)project["name"]);
        Assert.Equal(96, (int?)project["issueCount"]);
        Assert.Equal(22, (int?)project["activeIssueCount"]);

        var dataDir = root["dataDir"] as JsonObject;
        Assert.Equal("/home/.mohist", (string?)dataDir!["path"]);
        Assert.Equal("412 MB", (string?)dataDir["size"]);

        Assert.Null((string?)root["platformNotice"]);
    }
}
