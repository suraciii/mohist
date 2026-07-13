using System.Text.Json.Nodes;
using Mohist.Cli;
using Xunit;

namespace Mohist.Cli.UnitTests;

public class InfoRendererJsonTests
{
    [Fact]
    public void RenderJson_Default_ServerIncludesNestedStatusSourceGitObjects()
    {
        var renderer = CreateRenderer();
        var result = new InfoResult(
            Cli: new InfoCli("1.0.0", "/usr/bin/mo"),
            Server: new InfoService(
                new InfoServiceStatus("active", 1234, "5m"),
                new InfoSource("/repo", "a1b2c3d", "Add info")),
            Runner: new InfoService(null, null),
            Project: null,
            DataDir: new InfoDataDir("/d", null),
            PlatformNotice: null);

        var writer = new StringWriter();
        renderer.RenderJson(writer, result);

        var node = JsonNode.Parse(writer.ToString()) as JsonObject;
        var server = node!["server"] as JsonObject;
        Assert.NotNull(server);
        Assert.True(server!.ContainsKey("status"));
        Assert.True(server.ContainsKey("source"));

        var status = server["status"] as JsonObject;
        Assert.NotNull(status);
        Assert.Equal("active", (string?)status!["state"]);
        Assert.Equal(1234, (int?)status["pid"]);
        Assert.Equal("5m", (string?)status["uptime"]);

        var source = server["source"] as JsonObject;
        Assert.NotNull(source);
        Assert.Equal("/repo", (string?)source!["path"]);
        Assert.Equal("a1b2c3d", (string?)source["commitShort"]);
        Assert.Equal("Add info", (string?)source["commitSubject"]);
    }

    [Fact]
    public void RenderJson_ServiceNotRunning_StatusShowsNotRunningAndPidNull()
    {
        var renderer = CreateRenderer();
        var result = new InfoResult(
            Cli: new InfoCli("1.0.0", "/usr/bin/mo"),
            Server: new InfoService(
                new InfoServiceStatus("inactive", 0, null),
                null),
            Runner: new InfoService(null, null),
            Project: null,
            DataDir: new InfoDataDir("/d", null),
            PlatformNotice: null);

        var writer = new StringWriter();
        renderer.RenderJson(writer, result);

        var node = JsonNode.Parse(writer.ToString()) as JsonObject;
        var status = node!["server"]!["status"] as JsonObject;
        Assert.Equal("inactive", (string?)status!["state"]);
        Assert.Null((int?)status["pid"]);
        Assert.Equal(SystemdUnitParser.Unknown, (string?)status["uptime"]);
    }

    [Fact]
    public void RenderJson_ServiceNotInstalled_StateShowsNotInstalledSentinel()
    {
        var renderer = CreateRenderer();
        var result = new InfoResult(
            Cli: new InfoCli("1.0.0", "/usr/bin/mo"),
            Server: new InfoService(new InfoServiceStatus(SystemdUnitParser.NotInstalled, null, null, null, null), null),
            Runner: new InfoService(null, null),
            Project: null,
            DataDir: new InfoDataDir("/d", null),
            PlatformNotice: null);

        var writer = new StringWriter();
        renderer.RenderJson(writer, result);

        var node = JsonNode.Parse(writer.ToString()) as JsonObject;
        var status = node!["server"]!["status"] as JsonObject;
        Assert.Equal(SystemdUnitParser.NotInstalled, (string?)status!["state"]);
    }

    [Fact]
    public void RenderJson_ServiceStatusNull_UsesUnknownSentinelAndNullPid()
    {
        var renderer = CreateRenderer();
        var result = new InfoResult(
            Cli: new InfoCli("1.0.0", "/usr/bin/mo"),
            Server: new InfoService(null, null),
            Runner: new InfoService(null, null),
            Project: null,
            DataDir: new InfoDataDir("/d", null),
            PlatformNotice: null);

        var writer = new StringWriter();
        renderer.RenderJson(writer, result);

        var node = JsonNode.Parse(writer.ToString()) as JsonObject;
        var status = node!["server"]!["status"] as JsonObject;
        Assert.Equal(SystemdUnitParser.Unknown, (string?)status!["state"]);
        Assert.Null((int?)status["pid"]);
        Assert.Equal(SystemdUnitParser.Unknown, (string?)status["uptime"]);
    }

    [Fact]
    public void RenderJson_SourceMissing_RendersAsNull()
    {
        var renderer = CreateRenderer();
        var result = new InfoResult(
            Cli: new InfoCli("1.0.0", "/usr/bin/mo"),
            Server: new InfoService(new InfoServiceStatus("active", 1, "1m"), null),
            Runner: new InfoService(null, null),
            Project: null,
            DataDir: new InfoDataDir("/d", null),
            PlatformNotice: null);

        var writer = new StringWriter();
        renderer.RenderJson(writer, result);

        var node = JsonNode.Parse(writer.ToString()) as JsonObject;
        Assert.Null(node!["server"]!["source"]);
    }

    [Fact]
    public void RenderJson_ProjectMissing_RendersAsNull()
    {
        var renderer = CreateRenderer();
        var result = new InfoResult(
            Cli: new InfoCli("1.0.0", "/usr/bin/mo"),
            Server: new InfoService(new InfoServiceStatus("active", 1, "1m"), null),
            Runner: new InfoService(null, null),
            Project: null,
            DataDir: new InfoDataDir("/d", null),
            PlatformNotice: null);

        var writer = new StringWriter();
        renderer.RenderJson(writer, result);

        var node = JsonNode.Parse(writer.ToString()) as JsonObject;
        Assert.Null(node!["project"]);
    }

    [Fact]
    public void RenderJson_SourceNotGitRepo_RendersCommitShortAsNotAGitRepoSentinel()
    {
        var renderer = CreateRenderer();
        var result = new InfoResult(
            Cli: new InfoCli("1.0.0", "/usr/bin/mo"),
            Server: new InfoService(
                new InfoServiceStatus("active", 1, "1m"),
                new InfoSource("/repo", null, null)),
            Runner: new InfoService(null, null),
            Project: null,
            DataDir: new InfoDataDir("/d", null),
            PlatformNotice: null);

        var writer = new StringWriter();
        renderer.RenderJson(writer, result);

        var node = JsonNode.Parse(writer.ToString()) as JsonObject;
        var source = node!["server"]!["source"] as JsonObject;
        Assert.Equal("/repo", (string?)source!["path"]);
        Assert.Equal(SystemdUnitParser.NotAGitRepo, (string?)source["commitShort"]);
        Assert.Null((string?)source["commitSubject"]);
    }

    [Fact]
    public void RenderJson_DataDirSizeMissing_RendersUnknownSentinel()
    {
        var renderer = CreateRenderer();
        var result = new InfoResult(
            Cli: new InfoCli("1.0.0", "/usr/bin/mo"),
            Server: new InfoService(new InfoServiceStatus("active", 1, "1m"), null),
            Runner: new InfoService(null, null),
            Project: null,
            DataDir: new InfoDataDir("/home/.mohist", null),
            PlatformNotice: null);

        var writer = new StringWriter();
        renderer.RenderJson(writer, result);

        var node = JsonNode.Parse(writer.ToString()) as JsonObject;
        var dataDir = node!["dataDir"] as JsonObject;
        Assert.Equal("/home/.mohist", (string?)dataDir!["path"]);
        Assert.Equal(SystemdUnitParser.Unknown, (string?)dataDir["size"]);
    }

    [Fact]
    public void RenderJson_Verbose_IncludesAllVerboseSections()
    {
        var renderer = CreateRenderer();
        var verbose = new InfoVerbose(
            Skills: new InfoVerboseSkills(
            [
                new("mohist", "/skills/mohist"),
                new("mohist-explore", "/skills/mohist-explore"),
            ], Resolved: true),
            GitRemote: new InfoVerboseGitRemote("https://github.com/suraciii/mohist.git", IsGitRepo: true),
            OpencodeRuntime: new InfoVerboseOpencodeRuntime("opencode", "1.2.3", 5, Resolved: true),
            EnvVars:
            [
                new("RUNNER_ID", "r1"),
            ],
            OsRuntime: new InfoVerboseOsRuntime("linux", "x64", ".NET 11.0", "v22.5.0"),
            Capacity: new InfoVerboseCapacity(2),
            DiskUsage: new InfoVerboseDiskUsage(
            [
                new("logs", "2M", 4),
                new("projects", "10M", 7),
                new("worktrees", null, 0),
            ], Resolved: true));

        var result = new InfoResult(
            Cli: new InfoCli("1.0.0", "/usr/bin/mo"),
            Server: new InfoService(new InfoServiceStatus("active", 1, "1m"), new InfoSource("/r", "abc", "msg")),
            Runner: new InfoService(new InfoServiceStatus("active", 2, "1m"), new InfoSource("/r", "abc", "msg")),
            Project: new InfoProject("proj_1", "mohist-local", 1, 0),
            DataDir: new InfoDataDir("/d", "1M"),
            PlatformNotice: null,
            Verbose: verbose);

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

        var skills = node["skills"] as JsonArray;
        Assert.NotNull(skills);
        Assert.Equal(2, skills!.Count);

        var gitRemote = node["gitRemote"] as JsonObject;
        Assert.Equal("https://github.com/suraciii/mohist.git", (string?)gitRemote!["originUrl"]);
        Assert.True((bool?)gitRemote["isGitRepo"]);

        var opencode = node["opencodeRuntime"] as JsonObject;
        Assert.Equal("opencode", (string?)opencode!["command"]);
        Assert.Equal("1.2.3", (string?)opencode["version"]);
        Assert.Equal(5, (int?)opencode["modelCount"]);

        var envVars = node["envVars"] as JsonArray;
        Assert.NotNull(envVars);
        Assert.Single(envVars!);

        var osRuntime = node["osRuntime"] as JsonObject;
        Assert.Equal("linux", (string?)osRuntime!["os"]);
        Assert.Equal("x64", (string?)osRuntime["architecture"]);
        Assert.Equal(".NET 11.0", (string?)osRuntime["dotnetVersion"]);
        Assert.Equal("v22.5.0", (string?)osRuntime["nodeVersion"]);

        var capacity = node["capacity"] as JsonObject;
        Assert.Equal(2, (int?)capacity!["activeWorkflows"]);

        var diskUsage = node["diskUsage"] as JsonArray;
        Assert.NotNull(diskUsage);
        Assert.Equal(3, diskUsage!.Count);
    }

    [Fact]
    public void RenderJson_NoVerbose_DoesNotIncludeVerboseSections()
    {
        var renderer = CreateRenderer();
        var result = new InfoResult(
            Cli: new InfoCli("1.0.0", "/usr/bin/mo"),
            Server: new InfoService(new InfoServiceStatus("active", 1, "1m"), null),
            Runner: new InfoService(null, null),
            Project: null,
            DataDir: new InfoDataDir("/d", "1M"),
            PlatformNotice: null,
            Verbose: null);

        var writer = new StringWriter();
        renderer.RenderJson(writer, result);

        var node = JsonNode.Parse(writer.ToString()) as JsonObject;
        var keys = node!.Select(kv => kv.Key).ToHashSet();
        Assert.DoesNotContain("skills", keys);
        Assert.DoesNotContain("gitRemote", keys);
        Assert.DoesNotContain("opencodeRuntime", keys);
        Assert.DoesNotContain("envVars", keys);
        Assert.DoesNotContain("osRuntime", keys);
        Assert.DoesNotContain("capacity", keys);
        Assert.DoesNotContain("diskUsage", keys);
    }

    [Fact]
    public void RenderJson_VerboseSkills_MissingInstallPath_RendersUnknown()
    {
        var renderer = CreateRenderer();
        var verbose = new InfoVerbose(
            Skills: new InfoVerboseSkills([new("mohist", null)], Resolved: true),
            GitRemote: new InfoVerboseGitRemote(null, IsGitRepo: false),
            OpencodeRuntime: new InfoVerboseOpencodeRuntime(null, null, null, Resolved: false),
            EnvVars: [],
            OsRuntime: new InfoVerboseOsRuntime(null, null, null, null),
            Capacity: new InfoVerboseCapacity(null),
            DiskUsage: new InfoVerboseDiskUsage([], Resolved: true));

        var result = new InfoResult(
            Cli: new InfoCli("1.0.0", "/usr/bin/mo"),
            Server: new InfoService(null, null),
            Runner: new InfoService(null, null),
            Project: null,
            DataDir: new InfoDataDir("/d", null),
            PlatformNotice: null,
            Verbose: verbose);

        var writer = new StringWriter();
        renderer.RenderJson(writer, result);

        var node = JsonNode.Parse(writer.ToString()) as JsonObject;
        var skills = node!["skills"] as JsonArray;
        var skill = skills![0] as JsonObject;
        Assert.Equal("mohist", (string?)skill!["name"]);
        Assert.Equal(SystemdUnitParser.Unknown, (string?)skill["installPath"]);
    }

    [Fact]
    public void RenderJson_VerboseDiskCategorySizeMissing_RendersUnknown()
    {
        var renderer = CreateRenderer();
        var verbose = new InfoVerbose(
            Skills: new InfoVerboseSkills([], Resolved: true),
            GitRemote: new InfoVerboseGitRemote(null, IsGitRepo: false),
            OpencodeRuntime: new InfoVerboseOpencodeRuntime(null, null, null, Resolved: false),
            EnvVars: [],
            OsRuntime: new InfoVerboseOsRuntime(null, null, null, null),
            Capacity: new InfoVerboseCapacity(null),
            DiskUsage: new InfoVerboseDiskUsage([new("worktrees", null, null)], Resolved: true));

        var result = new InfoResult(
            Cli: new InfoCli("1.0.0", "/usr/bin/mo"),
            Server: new InfoService(null, null),
            Runner: new InfoService(null, null),
            Project: null,
            DataDir: new InfoDataDir("/d", null),
            PlatformNotice: null,
            Verbose: verbose);

        var writer = new StringWriter();
        renderer.RenderJson(writer, result);

        var node = JsonNode.Parse(writer.ToString()) as JsonObject;
        var disk = node!["diskUsage"] as JsonArray;
        var cat = disk![0] as JsonObject;
        Assert.Equal(SystemdUnitParser.Unknown, (string?)cat!["size"]);
        Assert.Null((int?)cat["fileCount"]);
    }

    [Fact]
    public void BuildJsonObject_RunnerStatus_StateStaysCleanWithConnectivityAsSeparateField()
    {
        var renderer = CreateRenderer();
        var result = new InfoResult(
            Cli: new InfoCli("1.0.0", "/usr/bin/mo"),
            Server: new InfoService(new InfoServiceStatus("active", 1, "1m"), null),
            Runner: new InfoService(
                new InfoServiceStatus("active", 2, "1m", UptimeSeconds: 60, Connectivity: "server ok"),
                null),
            Project: null,
            DataDir: new InfoDataDir("/d", "1M"),
            PlatformNotice: null);

        var root = InfoRenderer.BuildJsonObject(result);
        var runnerStatus = root["runner"]!["status"] as JsonObject;

        Assert.Equal("active", (string?)runnerStatus!["state"]);
        Assert.Equal(60L, (long?)runnerStatus["uptimeSeconds"]);
        Assert.Equal("server ok", (string?)runnerStatus["connectivity"]);
    }

    private static InfoRenderer CreateRenderer() => new();
}
