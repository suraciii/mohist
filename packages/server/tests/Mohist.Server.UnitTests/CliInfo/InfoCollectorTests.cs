using System.Net;
using System.Text;
using System.Text.Json;
using EnvironmentAbstractions.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Cli;
using Mohist.Server.UnitTests.Support;
using Xunit;

namespace Mohist.Server.UnitTests.CliInfo;

public class InfoCollectorTests
{
    [Fact]
    public async Task Collect_AllServicesActiveAndReachable_PopulatesAllFields()
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
        fs.AddFile(
            Path.Combine(repoDir, ".git", "HEAD"),
            "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2\n");

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

        var api = BuildApi(fs, commands, queueStatus: HttpStatusCode.OK, queueJson: """
            { "success": true, "data": { "name": "mohist-local", "issues": 96, "activeIssues": 22 } }
            """);
        var collector = new InfoCollector(fs, commands, new MockEnvironmentVariableProvider(), api, isSystemdAvailable: () => true);

        var result = await collector.CollectAsync();

        Assert.NotNull(result.Cli.Version);
        Assert.Equal("active", result.Server.Status!.State);
        Assert.Equal(1234, result.Server.Status.Pid);
        Assert.NotNull(result.Server.Status.Uptime);
        Assert.Equal("/repo", result.Server.Source!.Path);
        Assert.Equal("a1b2c3d", result.Server.Source.CommitShort);
        Assert.Equal("Add info command", result.Server.Source.CommitSubject);
        Assert.Equal(5678, result.Runner.Status!.Pid);
        Assert.Equal("active", result.Runner.Status.State);
        Assert.Equal(InfoCollector.ServerOk, result.Runner.Status.Connectivity);
    }

    [Fact]
    public async Task Collect_RunnerInactive_ShowsNotRunningAndSkipsConnectivity()
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
            ActiveState=inactive
            MainPID=0
            ExecMainStartTimestamp=
            FragmentPath=/units/mohist.service
            """);
        commands.Queue("git", 0, "a1b2c3d\n");
        commands.Queue("git", 0, "Add info command\n");
        commands.Queue("git", 0, "a1b2c3d\n");
        commands.Queue("git", 0, "Add info command\n");
        commands.Queue("du", 0, "412M\t/repo/.mohist\n");

        var api = BuildApi(fs, commands, queueStatus: HttpStatusCode.OK, queueJson: """
            { "success": true, "data": { "name": "mohist-local", "issues": 96, "activeIssues": 22 } }
            """);
        var collector = new InfoCollector(fs, commands, new MockEnvironmentVariableProvider(), api, isSystemdAvailable: () => true);

        var result = await collector.CollectAsync();

        Assert.Equal("active", result.Server.Status!.State);
        Assert.Equal("inactive", result.Runner.Status!.State);
        Assert.Null(result.Runner.Status.Connectivity);
    }

    [Fact]
    public async Task Collect_ServerUnreachable_ShowsServerUnreachableOnRunner()
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

        var api = BuildApi(fs, commands, queueStatus: HttpStatusCode.InternalServerError, queueJson: """
            { "success": false, "error": "boom" }
            """);
        var collector = new InfoCollector(fs, commands, new MockEnvironmentVariableProvider(), api, isSystemdAvailable: () => true);

        var result = await collector.CollectAsync();

        Assert.Equal(InfoCollector.ServerUnreachable, result.Runner.Status!.Connectivity);
    }

    [Fact]
    public async Task Collect_RunnerActiveAndReachable_StateIsClean()
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

        var api = BuildApi(fs, commands, queueStatus: HttpStatusCode.OK, queueJson: """
            { "success": true, "data": [] }
            """);
        var collector = new InfoCollector(fs, commands, new MockEnvironmentVariableProvider(), api, isSystemdAvailable: () => true);

        var result = await collector.CollectAsync();

        Assert.Equal("active", result.Runner.Status!.State);
        Assert.Equal(InfoCollector.ServerOk, result.Runner.Status.Connectivity);
    }

    [Fact]
    public async Task Collect_RunnerInactive_SkipsServerConnectivityHttpCall()
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
            ActiveState=inactive
            MainPID=0
            ExecMainStartTimestamp=
            FragmentPath=/units/mohist.service
            """);
        commands.Queue("git", 0, "a1b2c3d\n");
        commands.Queue("git", 0, "Add info command\n");
        commands.Queue("git", 0, "a1b2c3d\n");
        commands.Queue("git", 0, "Add info command\n");
        commands.Queue("du", 0, "412M\t/repo/.mohist\n");

        var handler = new FakeHttpHandler(HttpStatusCode.OK, """{ "success": true, "data": [] }""");
        var api = new MohistCliApi(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost:3456") },
            TextWriter.Null,
            TextWriter.Null,
            fs,
            commands);
        var collector = new InfoCollector(fs, commands, new MockEnvironmentVariableProvider(), api, isSystemdAvailable: () => true);

        var result = await collector.CollectAsync();

        Assert.Equal("inactive", result.Runner.Status!.State);
        Assert.Null(result.Runner.Status.Connectivity);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Collect_SourceIsNotAGitRepo_ShowsNotAGitRepoSentinel()
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
        // No .git directory
        fs.AddDirectory(repoDir);

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
        commands.Queue("du", 0, "12M\t/repo/.mohist\n");

        var api = BuildApi(fs, commands, queueStatus: HttpStatusCode.OK, queueJson: """
            { "success": true, "data": { "name": "mohist-local", "issues": 96, "activeIssues": 22 } }
            """);
        var collector = new InfoCollector(fs, commands, new MockEnvironmentVariableProvider(), api, isSystemdAvailable: () => true);

        var result = await collector.CollectAsync();

        Assert.Equal("/repo", result.Server.Source!.Path);
        Assert.Null(result.Server.Source.CommitShort);
        Assert.Null(result.Server.Source.CommitSubject);
    }

    [Fact]
    public async Task Collect_SourceDirectoryMissing_ShowsUnknownSentinel()
    {
        var unitDir = "/units";
        var fs = new FakeFileSystem();
        fs.AddFile(
            Path.Combine(unitDir, "mohist.service"),
            "[Service]\nWorkingDirectory=/does/not/exist\nExecStart=dotnet run --project /does/not/exist/Mohist.Server.csproj\n");
        fs.AddFile(
            Path.Combine(unitDir, "mohist-runner.service"),
            "[Service]\nWorkingDirectory=/also/missing\nExecStart=node packages/runner/dist/cli.js\n");

        var commands = new RecordingCommandExecutor();
        commands.Queue("systemctl", 0, """
            ActiveState=inactive
            MainPID=0
            ExecMainStartTimestamp=
            FragmentPath=/units/mohist.service
            """);
        commands.Queue("systemctl", 0, """
            ActiveState=inactive
            MainPID=0
            ExecMainStartTimestamp=
            FragmentPath=/units/mohist.service
            """);
        commands.Queue("du", 0, "12M\t/repo/.mohist\n");

        var api = BuildApi(fs, commands, queueStatus: HttpStatusCode.OK, queueJson: """
            { "success": true, "data": { "name": "mohist-local", "issues": 96, "activeIssues": 22 } }
            """);
        var collector = new InfoCollector(fs, commands, new MockEnvironmentVariableProvider(), api, isSystemdAvailable: () => true);

        var result = await collector.CollectAsync();

        Assert.Equal("inactive", result.Server.Status!.State);
        Assert.Equal("inactive", result.Runner.Status!.State);
        Assert.Null(result.Server.Source);
        Assert.Null(result.Runner.Source);
    }

    [Fact]
    public async Task Collect_SystemctlUnitMissing_HandlesFailSafe()
    {
        var fs = new FakeFileSystem();
        var commands = new RecordingCommandExecutor();
        commands.Queue("systemctl", 1, "Unit mohist.service not found.\n");
        commands.Queue("systemctl", 1, "Unit mohist-runner.service not found.\n");
        commands.Queue("du", 0, "12M\t/repo/.mohist\n");

        var api = BuildApi(fs, commands, queueStatus: HttpStatusCode.OK, queueJson: """
            { "success": true, "data": { "name": "mohist-local", "issues": 0, "activeIssues": 0 } }
            """);
        var collector = new InfoCollector(fs, commands, new MockEnvironmentVariableProvider(), api, isSystemdAvailable: () => true);

        var result = await collector.CollectAsync();

        Assert.NotNull(result.Server.Status);
        Assert.NotNull(result.Runner.Status);
        Assert.Equal(SystemdUnitParser.NotInstalled, result.Server.Status!.State);
        Assert.Equal(SystemdUnitParser.NotInstalled, result.Runner.Status!.State);

        var writer = new StringWriter();
        var renderer = new InfoRenderer();
        renderer.RenderDefault(writer, result);
        var text = writer.ToString();
        Assert.Contains(SystemdUnitParser.NotInstalled, text);
    }

    [Fact]
    public async Task Collect_SystemdUnavailable_DegradesGracefullyWithPlatformNotice()
    {
        var fs = new FakeFileSystem();
        var commands = new RecordingCommandExecutor();
        commands.Queue("du", 0, "12M\t/repo/.mohist\n");

        var api = BuildApi(fs, commands, queueStatus: HttpStatusCode.OK, queueJson: "{}");
        var collector = new InfoCollector(fs, commands, new MockEnvironmentVariableProvider(), api, isSystemdAvailable: () => false);

        var result = await collector.CollectAsync();

        Assert.Null(result.Server.Status);
        Assert.Null(result.Runner.Status);
        Assert.Null(result.Server.Source);
        Assert.Null(result.Runner.Source);

        var writer = new StringWriter();
        var renderer = new InfoRenderer();
        renderer.RenderDefault(writer, result);
        var text = writer.ToString();

        Assert.Contains("Server", text);
        Assert.Contains("Runner", text);
        Assert.Contains(InfoCollector.PlatformNoticeMessage, text);
        Assert.DoesNotContain("systemctl", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Collect_IsReadOnly_DoesNotInvokeMutatingCommands()
    {
        var fs = new FakeFileSystem();
        var commands = new RecordingCommandExecutor();
        commands.Queue("systemctl", 0, "ActiveState=inactive\nMainPID=0\nExecMainStartTimestamp=\nFragmentPath=\n");
        commands.Queue("systemctl", 0, "ActiveState=inactive\nMainPID=0\nExecMainStartTimestamp=\nFragmentPath=\n");
        commands.Queue("du", 0, "12M\t/repo/.mohist\n");

        var api = BuildApi(fs, commands, queueStatus: HttpStatusCode.OK, queueJson: "{}");
        var collector = new InfoCollector(fs, commands, new MockEnvironmentVariableProvider(), api, isSystemdAvailable: () => true);

        await collector.CollectAsync();

        var mutatingSubcommands = new[] { "install", "stop", "restart", "enable", "disable", "uninstall" };
        var allowedCommandVerbs = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["systemctl"] = new[] { "show" },
            ["git"] = new[] { "rev-parse", "log", "remote" },
            ["du"] = new[] { "-sh" },
            ["opencode"] = new[] { "--version" },
            ["node"] = new[] { "--version" },
        };
        foreach (var invocation in commands.Invocations)
        {
            var fileName = invocation.FileName;
            Assert.Contains(fileName, allowedCommandVerbs.Keys);
            var args = string.Join(" ", invocation.Args);
            Assert.DoesNotContain(mutatingSubcommands, s => args.Contains(" " + s + " ", StringComparison.OrdinalIgnoreCase));
            foreach (var verb in allowedCommandVerbs[fileName])
            {
                Assert.Contains(verb, args, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void ResolveSourcePath_PrefersWorkingDirectory_ThenProjectFlag_ThenBinaryDir()
    {
        Assert.Equal("/workdir", InfoSourcePathResolver.ResolveSourcePath(new SystemdUnitParser.SystemdUnitFields("/workdir", "dotnet run --project /proj")));

        var fromProject = InfoSourcePathResolver.ResolveSourcePath(new SystemdUnitParser.SystemdUnitFields(null, "dotnet run --project /proj/Mohist.Server.csproj"));
        Assert.Equal("/proj", fromProject);

        var fromBinary = InfoSourcePathResolver.ResolveSourcePath(new SystemdUnitParser.SystemdUnitFields(null, "node /binary/script.js"));
        Assert.Equal("/binary", fromBinary);

        var noPath = InfoSourcePathResolver.ResolveSourcePath(new SystemdUnitParser.SystemdUnitFields(null, "dotnet run"));
        Assert.Null(noPath);
    }

    [Fact]
    public void BuildCliLine_FormatsVersionAndPath()
    {
        var line = InfoRenderer.BuildCliLine(new InfoCli("1.0.0", "/usr/bin/mo"));
        Assert.Contains("1.0.0", line);
        Assert.Contains("/usr/bin/mo", line);
    }

    [Fact]
    public void BuildServiceLine_InactiveState_ShowsNotRunning()
    {
        var line = InfoRenderer.BuildServiceLine("Server",
            new InfoService(new InfoServiceStatus("inactive", 0, null), null),
            includeSource: false);
        Assert.Contains(SystemdUnitParser.NotRunning, line);
    }

    [Fact]
    public void BuildSourceLine_NoGitRepo_ShowsNotAGitRepoSentinel()
    {
        var line = InfoRenderer.BuildSourceLine("  source",
            new InfoSource("/repo", null, null));
        Assert.Contains("/repo", line);
        Assert.Contains(SystemdUnitParser.NotAGitRepo, line);
    }

    [Fact]
    public void BuildSourceLine_CommitAvailable_IncludesShaAndSubject()
    {
        var line = InfoRenderer.BuildSourceLine("  source",
            new InfoSource("/repo", "a1b2c3d", "Add info command"));
        Assert.Contains("/repo", line);
        Assert.Contains("a1b2c3d", line);
        Assert.Contains("Add info command", line);
    }

    [Fact]
    public void BuildProjectLine_FormatsIssueCounts()
    {
        var line = InfoRenderer.BuildProjectLine(new InfoProject("proj_1", "mohist-local", 96, 22));
        Assert.Contains("mohist-local", line);
        Assert.Contains("96", line);
        Assert.Contains("22", line);
    }

    [Fact]
    public void BuildDataDirLine_FormatsPathAndSize()
    {
        var line = InfoRenderer.BuildDataDirLine(new InfoDataDir("/home/.mohist", "412 MB"));
        Assert.Contains("/home/.mohist", line);
        Assert.Contains("412 MB", line);
    }

    [Fact]
    public void BuildDataDirLine_UnknownSize_ShowsSentinel()
    {
        var line = InfoRenderer.BuildDataDirLine(new InfoDataDir("/home/.mohist", null));
        Assert.Contains("/home/.mohist", line);
        Assert.Contains(SystemdUnitParser.Unknown, line);
    }

    private static MohistCliApi BuildApi(IFileSystem fs, ICommandExecutor commands, HttpStatusCode queueStatus, string queueJson)
    {
        var handler = new FakeHttpHandler(queueStatus, queueJson);
        return new MohistCliApi(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost:3456") },
            TextWriter.Null,
            TextWriter.Null,
            fs,
            commands);
    }

    private sealed class NoopCommandExecutor : ICommandExecutor
    {
        public Task<(int ExitCode, string Stdout, string Stderr)> ExecuteAsync(string fileName, string[] args, string? workingDirectory = null, CancellationToken cancellationToken = default) =>
            Task.FromResult((0, "", ""));
    }

    private sealed class RecordingCommandExecutor : ICommandExecutor
    {
        private readonly Queue<CommandResult> _results = new();
        private readonly Dictionary<string, Queue<CommandResult>> _byFileName = new(StringComparer.Ordinal);
        private readonly List<CommandInvocation> _invocations = new();

        public IReadOnlyList<CommandInvocation> Invocations => _invocations;

        public void Queue(string fileName, int exitCode, string stdout)
        {
            var result = new CommandResult(exitCode, stdout, "");
            _results.Enqueue(result);
            if (!_byFileName.TryGetValue(fileName, out var bucket))
            {
                bucket = new Queue<CommandResult>();
                _byFileName[fileName] = bucket;
            }
            bucket.Enqueue(result);
        }

        public Task<(int ExitCode, string Stdout, string Stderr)> ExecuteAsync(string fileName, string[] args, string? workingDirectory = null, CancellationToken cancellationToken = default)
        {
            _invocations.Add(new CommandInvocation(fileName, args.ToArray(), workingDirectory));
            if (_byFileName.TryGetValue(fileName, out var bucket) && bucket.Count > 0)
            {
                var matched = bucket.Dequeue();
                return Task.FromResult((matched.ExitCode, matched.Stdout, matched.Stderr));
            }
            if (_results.Count == 0)
                return Task.FromResult((0, "", ""));
            var result = _results.Dequeue();
            return Task.FromResult((result.ExitCode, result.Stdout, result.Stderr));
        }
    }

    private sealed record CommandResult(int ExitCode, string Stdout, string Stderr);
    private sealed record CommandInvocation(string FileName, string[] Args, string? WorkingDirectory);

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        private readonly Queue<HttpResponseMessage> _responses = new();

        public FakeHttpHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
        }
    }
}

public class InfoCommandRegistrationTests
{
    [Fact]
    public void InfoCommand_IsRegistered_AsTopLevelCommand()
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

        Assert.Contains(root.Subcommands, c => c.Name == "info");
    }

    [Fact]
    public void InfoCommand_Help_DescribesEnvironmentOverview()
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

        Assert.Contains("environment", help, StringComparison.OrdinalIgnoreCase);
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
}
