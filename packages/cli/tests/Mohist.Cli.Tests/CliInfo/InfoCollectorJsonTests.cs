using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EnvironmentAbstractions.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Cli;
using Mohist.Cli.Tests.Compatibility;
using Xunit;

namespace Mohist.Cli.Tests.CliInfo;

public class InfoCollectorJsonTests
{
    [Fact]
    public void RenderJson_Default_ServerIncludesNestedStatusSourceGitObjects()
    {
        var (collector, renderer) = BuildCollector();
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
        var (collector, renderer) = BuildCollector();
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
        var (collector, renderer) = BuildCollector();
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
        var (collector, renderer) = BuildCollector();
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
        var (collector, renderer) = BuildCollector();
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
        var (collector, renderer) = BuildCollector();
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
        var (collector, renderer) = BuildCollector();
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
        var (collector, renderer) = BuildCollector();
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
        var (collector, renderer) = BuildCollector();
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
        var (collector, renderer) = BuildCollector();
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
        var (collector, renderer) = BuildCollector();
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
        var (collector, renderer) = BuildCollector();
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
        var (collector, renderer) = BuildCollector();
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

    [Fact]
    public async Task Collect_Json_Default_ProducesValidJsonWithAllKeys()
    {
        var unitDir = "/units";
        var repoDir = "/repo";
        var fs = new FakeFileSystem();
        fs.AddFile(
            Path.Combine(unitDir, "mohist.service"),
            $"[Service]\nWorkingDirectory={repoDir}\nExecStart=dotnet run --project {repoDir}/Mohist.Server.csproj\n");
        fs.AddFile(
            Path.Combine(unitDir, "mohist-runner.service"),
            $"[Service]\nWorkingDirectory={repoDir}\nExecStart=node packages/runner/dist/cli.js\n");
        fs.AddDirectory(Path.Combine(repoDir, ".git"));

        var commands = new RecordingCommandExecutor();
        commands.Queue("systemctl", 0, """
            ActiveState=active
            MainPID=1234
            ExecMainStartTimestamp=Mon 2026-01-01 10:00:00 UTC
            FragmentPath=/units/mohist.service
            """);
        commands.Queue("systemctl", 0, """
            ActiveState=active
            MainPID=5678
            ExecMainStartTimestamp=Mon 2026-01-01 10:05:00 UTC
            FragmentPath=/units/mohist.service
            """);
        commands.Queue("git", 0, "a1b2c3d\n");
        commands.Queue("git", 0, "Add info command\n");
        commands.Queue("git", 0, "a1b2c3d\n");
        commands.Queue("git", 0, "Add info command\n");
        commands.Queue("du", 0, "412M\t/repo/.mohist\n");

        var pathHandler = new PathAwareHandler();
        pathHandler.Register("/api/projects",
            HttpStatusCode.OK, """{ "success": true, "data": [] }""");
        pathHandler.Register("/api/projects/proj_1/status",
            HttpStatusCode.OK, """{ "success": true, "data": { "name": "mohist-local", "issues": 96, "activeIssues": 22 } }""");

        var api = new MohistCliApi(
            new HttpClient(pathHandler) { BaseAddress = new Uri("http://localhost:3456") },
            TextWriter.Null,
            TextWriter.Null,
            fs,
            commands);
        api.ProjectStatePathOverride = () => Path.Combine(repoDir, ".mohist", "cli-state.json");

        var env = new MockEnvironmentVariableProvider();
        env["HOME"] = "/repo";
        fs.AddFile(
            Path.Combine(repoDir, ".mohist", "cli-state.json"),
            """{ "activeProjectId": "proj_1" }""");

        var collector = new InfoCollector(fs, commands, api, env, isSystemdAvailable: () => true);

        var result = await collector.CollectAsync();
        var writer = new StringWriter();
        var renderer = new InfoRenderer();
        renderer.RenderJson(writer, result);

        var text = writer.ToString();
        var node = JsonNode.Parse(text) as JsonObject;
        Assert.NotNull(node);
        var keys = node!.Select(kv => kv.Key).ToHashSet();
        Assert.Contains("cli", keys);
        Assert.Contains("server", keys);
        Assert.Contains("runner", keys);
        Assert.Contains("project", keys);
        Assert.Contains("dataDir", keys);
    }

    [Fact]
    public void InfoCommand_Help_DescribesJsonOption()
    {
        var services = new ServiceCollection();
        var api = new MohistCliApi(RejectingHttpMessageHandler.CreateClient(), TextWriter.Null, TextWriter.Null, new FakeFileSystem(), new NoopCommandExecutor());
        services.AddSingleton(api);
        services.AddSingleton(TextWriter.Null);
        services.AddSingleton<IFileSystem>(new FakeFileSystem());
        services.AddSingleton<ICommandExecutor>(new NoopCommandExecutor());
        services.AddSingleton<IEnvironmentVariableProvider>(new MockEnvironmentVariableProvider());
        services.AddSingleton<IServiceInstaller>(sp => new SystemdServiceInstaller(TextWriter.Null, TextWriter.Null, new FakeFileSystem(), sp.GetRequiredService<ICommandExecutor>()));
        services.AddSingleton(RejectingHttpMessageHandler.CreateClient());
        services.AddSingleton<RuntimeConsistencyValidator>(sp => new RuntimeConsistencyValidator(
            sp.GetRequiredService<HttpClient>(),
            sp.GetRequiredService<ICommandExecutor>(),
            sp.GetRequiredService<IFileSystem>(),
            sp.GetRequiredService<IEnvironmentVariableProvider>(),
            TextWriter.Null));
        services.AddSingleton<ServiceReadinessProbe>(sp => new ServiceReadinessProbe(
            sp.GetRequiredService<HttpClient>(),
            TextWriter.Null));
        services.AddSingleton<RunnerRefreshVerifier>(sp => new RunnerRefreshVerifier(
            sp.GetRequiredService<HttpClient>(),
            sp.GetRequiredService<ICommandExecutor>(),
            sp.GetRequiredService<IFileSystem>()));
        services.AddSingleton<UpdateOperations>(sp => new UpdateOperations(TextWriter.Null, TextWriter.Null, sp.GetRequiredService<IServiceInstaller>(), sp.GetRequiredService<ICommandExecutor>(), sp.GetRequiredService<IFileSystem>(), sp.GetRequiredService<IEnvironmentVariableProvider>()));
        services.AddSingleton<UpdateOutcomeReporter>(sp => new UpdateOutcomeReporter(sp.GetRequiredService<HttpClient>(), TextWriter.Null));
        services.AddSingleton<SourceCodeUpdater>();
        services.AddSingleton<SkillAssetService>();
        services.AddSingleton<SkillInstallService>();
        services.AddSingleton<InfoVerboseCollector>();
        services.AddSingleton<InfoCollector>();
        services.AddSingleton<InfoRenderer>();
        services.AddSingleton<InfoRenderer>();
        var provider = services.BuildServiceProvider();

        var root = MohistCliCommands.Build(api, provider);
        var help = RenderHelp(root, ["info", "--help"]);

        Assert.Contains("--json", help, StringComparison.Ordinal);
    }

    private static (InfoCollector collector, InfoRenderer renderer) BuildCollector()
    {
        var api = new MohistCliApi(
            RejectingHttpMessageHandler.CreateClient(),
            TextWriter.Null,
            TextWriter.Null,
            new FakeFileSystem(),
            new NoopCommandExecutor());
        return (new InfoCollector(new FakeFileSystem(), new NoopCommandExecutor(), api, new MockEnvironmentVariableProvider()), new InfoRenderer());
    }

    private static MohistCliApi BuildApi(IFileSystem fs, ICommandExecutor commands, HttpStatusCode status, string body)
    {
        var handler = new FakeHttpHandler(status, body);
        return new MohistCliApi(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost:3456") },
            TextWriter.Null,
            TextWriter.Null,
            fs,
            commands);
    }

    private static string RenderHelp(global::System.CommandLine.RootCommand root, string[] args)
    {
        using var writer = new StringWriter();
        var config = new System.CommandLine.InvocationConfiguration { Output = writer, Error = writer };
        root.Parse(args).Invoke(config);
        return writer.ToString();
    }

    private sealed class NoopCommandExecutor : ICommandExecutor
    {
        public Task<(int ExitCode, string Stdout, string Stderr)> ExecuteAsync(string fileName, string[] args, string? workingDirectory = null, CancellationToken cancellationToken = default) =>
            Task.FromResult((0, "", ""));
    }

    private sealed class RecordingCommandExecutor : ICommandExecutor
    {
        private readonly Dictionary<string, Queue<CommandResult>> _byFileName = new(StringComparer.Ordinal);
        private readonly List<CommandInvocation> _invocations = new();

        public IReadOnlyList<CommandInvocation> Invocations => _invocations;

        public void Queue(string fileName, int exitCode, string stdout)
        {
            if (!_byFileName.TryGetValue(fileName, out var bucket))
            {
                bucket = new Queue<CommandResult>();
                _byFileName[fileName] = bucket;
            }
            bucket.Enqueue(new CommandResult(exitCode, stdout, ""));
        }

        public Task<(int ExitCode, string Stdout, string Stderr)> ExecuteAsync(string fileName, string[] args, string? workingDirectory = null, CancellationToken cancellationToken = default)
        {
            _invocations.Add(new CommandInvocation(fileName, args.ToArray(), workingDirectory));
            if (_byFileName.TryGetValue(fileName, out var bucket) && bucket.Count > 0)
            {
                var matched = bucket.Dequeue();
                return Task.FromResult((matched.ExitCode, matched.Stdout, matched.Stderr));
            }
            return Task.FromResult((0, "", ""));
        }
    }

    private sealed record CommandResult(int ExitCode, string Stdout, string Stderr);
    private sealed record CommandInvocation(string FileName, string[] Args, string? WorkingDirectory);

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public FakeHttpHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class PathAwareHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, HttpResponseMessage> _byPath = new(StringComparer.OrdinalIgnoreCase);

        public void Register(string path, HttpStatusCode status, string body)
        {
            _byPath[NormalizePath(path)] = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = NormalizePath(request.RequestUri?.AbsolutePath ?? string.Empty);
            if (_byPath.TryGetValue(path, out var response))
                return Task.FromResult(response);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
        }

        private static string NormalizePath(string path)
        {
            if (path.StartsWith('/'))
                return path;
            return "/" + path;
        }
    }
}
