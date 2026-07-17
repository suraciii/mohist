using System.Net;
using System.Text;
using EnvironmentAbstractions.TestHelpers;
using Mohist.Cli;
using Mohist.Cli.Tests.Compatibility;
using Xunit;

namespace Mohist.Cli.Tests.CliInfo;

public class InfoVerboseCollectorTests
{
    [Fact]
    public void SkillAssetService_GetSkill_WithValidTestRoot_ReturnsDirectoryPath()
    {
        var fs = new FakeFileSystem();
        AddSkill(fs, assetRoot: "/skills", "mohist");

        var skills = new SkillAssetService(fs, "/skills");
        var result = skills.GetSkill("mohist", includeSupplementaryFiles: false);

        Assert.True(result.Found, "GetSkill should succeed; actual error: " + result.Error);
        Assert.NotNull(result.Skill);
        Assert.Equal(Path.Combine("/skills", "mohist"), result.Skill!.DirectoryPath);
    }

    [Fact]
    public async Task GetSkillsVerboseAsync_WithValidTestRoot_ReturnsInstallPaths()
    {
        var fs = new FakeFileSystem();
        AddSkill(fs, assetRoot: "/skills", "mohist");

        var skills = new SkillAssetService(fs, "/skills");
        var collector = new InfoVerboseCollector(
            fs,
            new NoopCommandExecutor(),
            new MockEnvironmentVariableProvider(),
            BuildApi(fs, new NoopCommandExecutor(), HttpStatusCode.OK, "{}"),
            skills);

        var result = await collector.GetSkillsVerboseAsync();

        var mohist = Assert.Single(result.Skills, s => s.Name == "mohist");
        Assert.NotNull(mohist.InstallPath);
        Assert.Contains("mohist", mohist.InstallPath!);
    }

    [Fact]
    public async Task Verbose_Skills_ResolvesVisibleSkillsAndInstallPath()
    {
        var fs = new FakeFileSystem();
        AddSkill(fs, assetRoot: "/skills", "mohist");
        AddSkill(fs, assetRoot: "/skills", "mohist-explore");

        var skills = new SkillAssetService(fs, "/skills");
        var collector = new InfoVerboseCollector(
            fs,
            new NoopCommandExecutor(),
            new MockEnvironmentVariableProvider(),
            BuildApi(fs, new NoopCommandExecutor(), HttpStatusCode.OK, "{}"),
            skills);

        var result = await collector.GetSkillsVerboseAsync();

        Assert.NotEmpty(result.Skills);
        Assert.All(result.Skills, s => Assert.False(string.IsNullOrWhiteSpace(s.Name)));
        Assert.Contains(result.Skills, s => s.Name == "mohist");
        Assert.Contains(result.Skills, s => s.InstallPath is not null && s.InstallPath.Contains("mohist"));
    }

    [Fact]
    public async Task Verbose_Skills_NoSkillService_ReturnsEmptyButResolved()
    {
        var fs = new FakeFileSystem();
        var collector = new InfoVerboseCollector(
            fs,
            new NoopCommandExecutor(),
            new MockEnvironmentVariableProvider(),
            BuildApi(fs, new NoopCommandExecutor(), HttpStatusCode.OK, "{}"),
            skillAssetService: null);

        var result = await collector.GetSkillsVerboseAsync();

        Assert.Empty(result.Skills);
        Assert.True(result.Resolved);
    }

    [Fact]
    public async Task Verbose_GitRemote_WithOriginConfigured_ReturnsUrl()
    {
        var sourcePath = "/repo";
        var fs = new FakeFileSystem();
        fs.AddDirectory(sourcePath);
        fs.AddDirectory(Path.Combine(sourcePath, ".git"));

        var commands = new RecordingCommandExecutor();
        commands.Queue("git", 0, "git@github.com:suraciii/mohist.git\n");

        var collector = BuildVerboseCollector(fs, commands);

        var result = await collector.GetGitRemoteVerboseAsync(sourcePath);

        Assert.True(result.IsGitRepo);
        Assert.Equal("git@github.com:suraciii/mohist.git", result.OriginUrl);
    }

    [Fact]
    public async Task Verbose_GitRemote_NoOriginConfigured_ReturnsNullUrl()
    {
        var sourcePath = "/repo";
        var fs = new FakeFileSystem();
        fs.AddDirectory(sourcePath);
        fs.AddDirectory(Path.Combine(sourcePath, ".git"));

        var commands = new RecordingCommandExecutor();
        commands.Queue("git", 1, "fatal: No such remote 'origin'\n");

        var collector = BuildVerboseCollector(fs, commands);

        var result = await collector.GetGitRemoteVerboseAsync(sourcePath);

        Assert.True(result.IsGitRepo);
        Assert.Null(result.OriginUrl);
    }

    [Fact]
    public async Task Verbose_GitRemote_NotAGitRepo_ReturnsNullAndIsNotGit()
    {
        var sourcePath = "/repo";
        var fs = new FakeFileSystem();
        fs.AddDirectory(sourcePath);

        var collector = BuildVerboseCollector(fs, new NoopCommandExecutor());

        var result = await collector.GetGitRemoteVerboseAsync(sourcePath);

        Assert.False(result.IsGitRepo);
        Assert.Null(result.OriginUrl);
    }

    [Fact]
    public async Task Verbose_GitRemote_MissingSourcePath_ReturnsNullAndIsNotGit()
    {
        var fs = new FakeFileSystem();

        var collector = BuildVerboseCollector(fs, new NoopCommandExecutor());

        var result = await collector.GetGitRemoteVerboseAsync("/does/not/exist");

        Assert.False(result.IsGitRepo);
        Assert.Null(result.OriginUrl);
    }

    [Fact]
    public async Task Verbose_OpencodeRuntime_ResolvesCommandVersionAndModelCount()
    {
        var commands = new RecordingCommandExecutor();
        commands.Queue("opencode", 0, "opencode 1.2.3\n");
        var api = BuildApi(fs: new FakeFileSystem(), commands, HttpStatusCode.OK,
            """{ "success": true, "data": { "command": "opencode", "models": ["m1", "m2", "m3"] } }""");

        var collector = new InfoVerboseCollector(new FakeFileSystem(), commands, new MockEnvironmentVariableProvider(), api);

        var result = await collector.GetOpencodeRuntimeVerboseAsync();

        Assert.Equal("opencode", result.Command);
        Assert.Equal("opencode 1.2.3", result.Version);
        Assert.Equal(3, result.ModelCount);
        Assert.True(result.Resolved);
    }

    [Fact]
    public async Task Verbose_OpencodeRuntime_ServerUnreachable_FallsBackToUnknown()
    {
        var commands = new RecordingCommandExecutor();
        commands.Queue("opencode", 1, "");
        var api = BuildApi(fs: new FakeFileSystem(), commands, HttpStatusCode.InternalServerError, "{}");

        var collector = new InfoVerboseCollector(new FakeFileSystem(), commands, new MockEnvironmentVariableProvider(), api);

        var result = await collector.GetOpencodeRuntimeVerboseAsync();

        Assert.Null(result.Version);
        Assert.Null(result.ModelCount);
    }

    [Fact]
    public async Task Verbose_OpencodeRuntime_UsesEnvOverrideForCommand()
    {
        var env = new MockEnvironmentVariableProvider();
        env["MOHIST_AGENT_COMMAND"] = "opencode";
        var commands = new RecordingCommandExecutor();
        commands.Queue("opencode", 0, "v9.9.9\n");
        var api = BuildApi(fs: new FakeFileSystem(), commands, HttpStatusCode.OK, "{}");

        var collector = new InfoVerboseCollector(new FakeFileSystem(), commands, env, api);

        var result = await collector.GetOpencodeRuntimeVerboseAsync();

        Assert.Equal("opencode", result.Command);
        Assert.Equal("v9.9.9", result.Version);
    }

    [Fact]
    public async Task Verbose_OpencodeRuntime_RejectsUnknownCommand()
    {
        var env = new MockEnvironmentVariableProvider();
        env["MOHIST_AGENT_COMMAND"] = "custom-opencode";
        var commands = new RecordingCommandExecutor();
        var api = BuildApi(fs: new FakeFileSystem(), commands, HttpStatusCode.OK, "{}");

        var collector = new InfoVerboseCollector(new FakeFileSystem(), commands, env, api);

        var result = await collector.GetOpencodeRuntimeVerboseAsync();

        Assert.Null(result.Command);
        Assert.Null(result.Version);
        Assert.Empty(commands.Invocations);
    }

    [Fact]
    public async Task Verbose_EnvVars_ReadsFromSystemdUnitEnvironment()
    {
        var commands = new RecordingCommandExecutor();
        commands.Queue("systemctl", 0, """
            Environment=RUNNER_ID=runner-1 SERVER_URL=http://localhost:3456 PATH=/usr/bin
            """);
        var runner = new InfoService(new InfoServiceStatus("active", 1234, "5m"), null);

        var collector = new InfoVerboseCollector(
            new FakeFileSystem(),
            commands,
            new MockEnvironmentVariableProvider(),
            BuildApi(new FakeFileSystem(), commands, HttpStatusCode.OK, "{}"));

        var result = await collector.GetEnvVarsVerboseAsync(runner, systemdAvailable: true);

        Assert.Contains(result, e => e.Name == "RUNNER_ID" && e.Value == "runner-1");
        Assert.Contains(result, e => e.Name == "SERVER_URL" && e.Value == "http://localhost:3456");
        Assert.DoesNotContain(result, e => e.Name == "PATH");
    }

    [Fact]
    public async Task Verbose_EnvVars_ReadsFromProcessEnvironmentWhenNoSystemd()
    {
        var env = new MockEnvironmentVariableProvider();
        env["RUNNER_ID"] = "fallback-runner";
        env["SERVER_URL"] = "http://example.test:1234";

        var collector = new InfoVerboseCollector(
            new FakeFileSystem(),
            new NoopCommandExecutor(),
            env,
            BuildApi(new FakeFileSystem(), new NoopCommandExecutor(), HttpStatusCode.OK, "{}"));

        var runner = new InfoService(null, null);
        var result = await collector.GetEnvVarsVerboseAsync(runner, systemdAvailable: false);

        Assert.Contains(result, e => e.Name == "RUNNER_ID" && e.Value == "fallback-runner");
        Assert.Contains(result, e => e.Name == "SERVER_URL" && e.Value == "http://example.test:1234");
    }

    [Fact]
    public async Task Verbose_OsRuntime_ReportsNodeVersion()
    {
        var commands = new RecordingCommandExecutor();
        commands.Queue("node", 0, "v22.5.0\n");

        var collector = new InfoVerboseCollector(
            new FakeFileSystem(),
            commands,
            new MockEnvironmentVariableProvider(),
            BuildApi(new FakeFileSystem(), commands, HttpStatusCode.OK, "{}"));

        var result = await collector.GetOsRuntimeVerboseAsync();

        Assert.NotNull(result.Os);
        Assert.NotNull(result.Architecture);
        Assert.NotNull(result.DotnetVersion);
        Assert.Equal("v22.5.0", result.NodeVersion);
    }

    [Fact]
    public async Task Verbose_OsRuntime_NodeMissing_LeavesNull()
    {
        var commands = new RecordingCommandExecutor();
        commands.Queue("node", 1, "");

        var collector = new InfoVerboseCollector(
            new FakeFileSystem(),
            commands,
            new MockEnvironmentVariableProvider(),
            BuildApi(new FakeFileSystem(), commands, HttpStatusCode.OK, "{}"));

        var result = await collector.GetOsRuntimeVerboseAsync();

        Assert.Null(result.NodeVersion);
    }

    [Fact]
    public async Task Verbose_Capacity_ReadsActiveFromServerOnly()
    {
        var commands = new RecordingCommandExecutor();
        commands.Queue("systemctl", 0, """
            Environment=RUNNER_ID=r1 SERVER_URL=http://localhost:3456
            """);
        var handler = new MultiResponseHandler(new[]
        {
            (HttpStatusCode.OK, """{ "success": true, "data": { "capacity": { "active": 2, "max": 8 } } }"""),
        });
        var api = new MohistCliApi(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost:3456") },
            TextWriter.Null,
            TextWriter.Null,
            new FakeFileSystem(),
            commands);

        var runner = new InfoService(new InfoServiceStatus("active", 1234, "5m"), null);
        var project = new InfoProject("proj_1", "mohist-local", 1, 0);

        var collector = new InfoVerboseCollector(new FakeFileSystem(), commands, new MockEnvironmentVariableProvider(), api);

        var result = await collector.GetCapacityVerboseAsync(runner, project, systemdAvailable: true);

        Assert.Equal(2, result.ActiveWorkflows);
    }

    [Fact]
    public async Task Verbose_Capacity_DoesNotExposeServerMax()
    {
        var commands = new RecordingCommandExecutor();
        commands.Queue("systemctl", 0, "Environment=RUNNER_ID=r1 SERVER_URL=http://localhost:3456\n");
        var handler = new MultiResponseHandler(new[]
        {
            (HttpStatusCode.OK, """{ "success": true, "data": { "capacity": { "active": 2, "max": 8 } } }"""),
        });
        var api = new MohistCliApi(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost:3456") },
            TextWriter.Null,
            TextWriter.Null,
            new FakeFileSystem(),
            commands);

        var runner = new InfoService(new InfoServiceStatus("active", 1234, "5m"), null);
        var project = new InfoProject("proj_1", "mohist-local", 1, 0);

        var collector = new InfoVerboseCollector(new FakeFileSystem(), commands, new MockEnvironmentVariableProvider(), api);

        var result = await collector.GetCapacityVerboseAsync(runner, project, systemdAvailable: true);

        Assert.Equal(2, result.ActiveWorkflows);
    }

    [Fact]
    public async Task Verbose_Capacity_NoProject_LeavesActiveUnknown()
    {
        var commands = new RecordingCommandExecutor();
        commands.Queue("systemctl", 0, """
            Environment=RUNNER_ID=r1
            """);
        var api = BuildApi(new FakeFileSystem(), commands, HttpStatusCode.OK, "{}");

        var runner = new InfoService(new InfoServiceStatus("active", 1234, "5m"), null);

        var collector = new InfoVerboseCollector(new FakeFileSystem(), commands, new MockEnvironmentVariableProvider(), api);

        var result = await collector.GetCapacityVerboseAsync(runner, project: null, systemdAvailable: true);

        Assert.Null(result.ActiveWorkflows);
    }

    [Fact]
    public async Task Verbose_Capacity_ServerUnreachable_LeavesActiveUnknown()
    {
        var commands = new RecordingCommandExecutor();
        commands.Queue("systemctl", 0, """
            Environment=RUNNER_ID=r1
            """);
        var api = BuildApi(new FakeFileSystem(), commands, HttpStatusCode.InternalServerError, "{}");

        var runner = new InfoService(new InfoServiceStatus("active", 1234, "5m"), null);
        var project = new InfoProject("proj_1", "mohist-local", 1, 0);

        var collector = new InfoVerboseCollector(new FakeFileSystem(), commands, new MockEnvironmentVariableProvider(), api);

        var result = await collector.GetCapacityVerboseAsync(runner, project, systemdAvailable: true);

        Assert.Null(result.ActiveWorkflows);
    }

    [Fact]
    public async Task Verbose_DiskUsage_ReportsEachCategorySize()
    {
        var fs = new FakeFileSystem();
        var dataDir = "/data/.mohist";
        fs.AddDirectory(dataDir);
        fs.AddDirectory(Path.Combine(dataDir, "projects"));
        fs.AddDirectory(Path.Combine(dataDir, "logs"));
        fs.AddDirectory(Path.Combine(dataDir, "worktrees"));
        fs.AddFile(Path.Combine(dataDir, "projects", "p1.txt"), "x");
        fs.AddFile(Path.Combine(dataDir, "logs", "a.log"), "abc");
        fs.AddFile(Path.Combine(dataDir, "logs", "b.log"), "de");

        var commands = new RecordingCommandExecutor();
        commands.Queue("du", 0, "10M\t/data/.mohist/projects\n");
        commands.Queue("du", 0, "2M\t/data/.mohist/logs\n");
        commands.Queue("du", 0, "100K\t/data/.mohist/worktrees\n");

        var collector = new InfoVerboseCollector(fs, commands, new MockEnvironmentVariableProvider(),
            BuildApi(fs, commands, HttpStatusCode.OK, "{}"));

        var data = new InfoDataDir(dataDir, "12M");
        var result = await collector.GetDiskUsageVerboseAsync(data);

        var projects = Assert.Single(result.Categories, c => c.Name == "projects");
        var logs = Assert.Single(result.Categories, c => c.Name == "logs");
        var worktrees = Assert.Single(result.Categories, c => c.Name == "worktrees");

        Assert.Equal("10M", projects.Size);
        Assert.Equal(1, projects.FileCount);

        Assert.Equal("2M", logs.Size);
        Assert.Equal(2, logs.FileCount);

        Assert.Equal("100K", worktrees.Size);
        Assert.Equal(0, worktrees.FileCount);
    }

    [Fact]
    public async Task Verbose_DiskUsage_MissingDataDir_ReturnsEmpty()
    {
        var fs = new FakeFileSystem();
        var collector = new InfoVerboseCollector(fs, new NoopCommandExecutor(), new MockEnvironmentVariableProvider(),
            BuildApi(fs, new NoopCommandExecutor(), HttpStatusCode.OK, "{}"));

        var result = await collector.GetDiskUsageVerboseAsync(new InfoDataDir("/missing/.mohist", null));

        Assert.Empty(result.Categories);
    }

    [Fact]
    public async Task Verbose_DiskUsage_DuTimesOut_LeavesSizeNull()
    {
        var fs = new FakeFileSystem();
        var dataDir = "/data/.mohist";
        fs.AddDirectory(dataDir);
        fs.AddDirectory(Path.Combine(dataDir, "projects"));

        var collector = new InfoVerboseCollector(fs, new NoopCommandExecutor(), new MockEnvironmentVariableProvider(),
            BuildApi(fs, new NoopCommandExecutor(), HttpStatusCode.OK, "{}"));

        var result = await collector.GetDiskUsageVerboseAsync(new InfoDataDir(dataDir, "5M"));

        var projects = Assert.Single(result.Categories, c => c.Name == "projects");
        Assert.Null(projects.Size);
    }

    [Fact]
    public void RenderVerbose_IncludesAllSectionHeaders()
    {
        var verbose = new InfoVerbose(
            Skills: new InfoVerboseSkills([], Resolved: true),
            GitRemote: new InfoVerboseGitRemote("https://example.test/repo.git", IsGitRepo: true),
            OpencodeRuntime: new InfoVerboseOpencodeRuntime("opencode", "1.0.0", 5, Resolved: true),
            EnvVars: [new InfoVerboseEnvVar("RUNNER_ID", "r1")],
            OsRuntime: new InfoVerboseOsRuntime("linux", "x64", ".NET 11.0", "v22.5.0"),
            Capacity: new InfoVerboseCapacity(1),
            DiskUsage: new InfoVerboseDiskUsage([new InfoVerboseDiskCategory("projects", "10M", 3)], Resolved: true));

        var writer = new StringWriter();
        var renderer = new InfoRenderer();
        renderer.RenderVerbose(writer, verbose);
        var text = writer.ToString();

        Assert.Contains("Skills:", text);
        Assert.Contains("Git remote:", text);
        Assert.Contains("Opencode runtime:", text);
        Assert.Contains("Environment variables:", text);
        Assert.Contains("OS / Runtime:", text);
        Assert.Contains("Runner capacity:", text);
        Assert.Contains("Disk usage breakdown:", text);
    }

    [Fact]
    public void RenderVerbose_UnknownFields_ShowSentinels()
    {
        var verbose = new InfoVerbose(
            Skills: new InfoVerboseSkills([], Resolved: true),
            GitRemote: new InfoVerboseGitRemote(null, IsGitRepo: false),
            OpencodeRuntime: new InfoVerboseOpencodeRuntime(null, null, null, Resolved: false),
            EnvVars: [],
            OsRuntime: new InfoVerboseOsRuntime(null, null, null, null),
            Capacity: new InfoVerboseCapacity(null),
            DiskUsage: new InfoVerboseDiskUsage([], Resolved: true));

        var writer = new StringWriter();
        var renderer = new InfoRenderer();
        renderer.RenderVerbose(writer, verbose);
        var text = writer.ToString();

        Assert.Contains("<unknown>", text);
    }

    [Fact]
    public void ParseSystemdEnvironment_ParsesSpaceSeparatedKeyValuePairs()
    {
        var output = """
            Environment=KEY1=value1 KEY2=value2 KEY3="quoted value with spaces" PATH=/usr/bin
            """;

        var result = SystemdUnitParser.ParseSystemdEnvironment(output);

        Assert.Equal("value1", result["KEY1"]);
        Assert.Equal("value2", result["KEY2"]);
        Assert.Equal("quoted value with spaces", result["KEY3"]);
        Assert.Equal("/usr/bin", result["PATH"]);
    }

    [Fact]
    public void BuildOriginUrl_NoUrlAndNotGitRepo_ReturnsUnknown()
    {
        var result = InfoRenderer.BuildOriginUrl(new InfoVerboseGitRemote(null, IsGitRepo: false));
        Assert.Equal(SystemdUnitParser.Unknown, result);
    }

    [Fact]
    public void BuildOriginUrl_NoUrlButIsGitRepo_ReturnsNotAGitRepo()
    {
        var result = InfoRenderer.BuildOriginUrl(new InfoVerboseGitRemote(null, IsGitRepo: true));
        Assert.Equal(SystemdUnitParser.NotAGitRepo, result);
    }

    [Fact]
    public void BuildSkillLines_OrdersByName()
    {
        var lines = InfoRenderer.BuildSkillLines(new InfoVerboseSkills(
        [
            new("zeta", "/path/zeta"),
            new("alpha", "/path/alpha"),
        ], Resolved: true)).ToArray();

        Assert.Equal(2, lines.Length);
        Assert.StartsWith("alpha", lines[0]);
        Assert.StartsWith("zeta", lines[1]);
    }

    [Fact]
    public void BuildEnvVarLines_FormatsNameEqualsValue()
    {
        var lines = InfoRenderer.BuildEnvVarLines([
            new("B", "2"),
            new("A", "1"),
        ]).ToArray();

        Assert.Equal(2, lines.Length);
        Assert.Equal("A=1", lines[0]);
        Assert.Equal("B=2", lines[1]);
    }

    [Fact]
    public void BuildDiskCategoryLines_FormatsSizeAndFileCount()
    {
        var lines = InfoRenderer.BuildDiskCategoryLines([
            new("logs", "2M", 4),
            new("projects", "10M", null),
            new("worktrees", null, null),
        ]).ToArray();

        Assert.Equal(3, lines.Length);
        Assert.Contains("logs", lines[0]);
        Assert.Contains("2M", lines[0]);
        Assert.Contains("4 files", lines[0]);
        Assert.Contains("projects", lines[1]);
        Assert.Contains("10M", lines[1]);
        Assert.DoesNotContain("files", lines[1]);
        Assert.Equal("worktrees", lines[2]);
    }

    private static void AddSkill(FakeFileSystem fs, string assetRoot, string name)
    {
        var skillDir = Path.Combine(assetRoot, name);
        fs.AddDirectory(skillDir);
        var description = name switch
        {
            "mohist" => "执行 Mohist 当前 .NET 后端/API/Web 相关操作。当用户要求创建、查看、启动、审批、关闭 issue 或 epic，查看项目状态或日志，或任何涉及 Mohist issue/epic/workflow 的操作时使用。旧 Node CLI 已移除。",
            "mohist-explore" => "把模糊的产品想法提炼成清晰的、有边界的 Mohist issue 需求文档。",
            _ => "skill",
        };
        fs.AddFile(Path.Combine(skillDir, "SKILL.md"), $"---\nname: {name}\ndescription: {description}\n---\n");
    }

    private static MohistCliApi BuildApi(IFileSystem fs, ICommandExecutor commands, HttpStatusCode status, string body)
    {
        var handler = new MultiResponseHandler(new[] { (status, body) });
        return new MohistCliApi(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost:3456") },
            TextWriter.Null,
            TextWriter.Null,
            fs,
            commands);
    }

    private static InfoVerboseCollector BuildVerboseCollector(IFileSystem fs, ICommandExecutor commands)
    {
        return new InfoVerboseCollector(
            fs,
            commands,
            new MockEnvironmentVariableProvider(),
            BuildApi(fs, commands, HttpStatusCode.OK, "{}"));
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

    private sealed class MultiResponseHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();

        public MultiResponseHandler((HttpStatusCode Status, string Body)[] responses)
        {
            foreach (var (status, body) in responses)
            {
                _responses.Enqueue(new HttpResponseMessage(status)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                });
            }
        }

        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (_responses.Count == 0)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json"),
                });
            return Task.FromResult(_responses.Dequeue());
        }
    }
}
