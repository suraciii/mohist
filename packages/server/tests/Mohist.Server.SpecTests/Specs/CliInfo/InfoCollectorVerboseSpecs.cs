using System.Net;
using System.Text;
using EnvironmentAbstractions.TestHelpers;
using Mohist.Cli;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.CliInfo;

public class InfoCollectorVerboseSpecs
{
    [Fact]
    public async Task Collect_VerboseTrue_PopulatesVerboseSection()
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
        fs.AddDirectory(repoDir);
        fs.AddDirectory(Path.Combine(repoDir, ".git"));
        fs.AddDirectory(Path.Combine(repoDir, ".mohist"));
        fs.AddDirectory(Path.Combine(repoDir, ".mohist", "projects"));
        fs.AddDirectory(Path.Combine(repoDir, ".mohist", "logs"));
        fs.AddDirectory(Path.Combine(repoDir, ".mohist", "worktrees"));

        var commands = new RecordingCommandExecutor();
        commands.Queue("systemctl", 0, """
            ActiveState=active
            MainPID=1234
            ExecMainStartTimestamp=Mon 2026-01-01 10:00:00 UTC
            FragmentPath=/units/mohist.service
            Environment=RUNNER_ID=r1 SERVER_URL=http://localhost:3456
            """);
        commands.Queue("systemctl", 0, """
            ActiveState=active
            MainPID=5678
            ExecMainStartTimestamp=Mon 2026-01-01 10:05:00 UTC
            FragmentPath=/units/mohist.service
            Environment=RUNNER_ID=r1 SERVER_URL=http://localhost:3456
            """);
        commands.Queue("systemctl", 0, """
            Environment=RUNNER_ID=r1 SERVER_URL=http://localhost:3456
            """);
        commands.Queue("git", 0, "a1b2c3d\n");
        commands.Queue("git", 0, "Add info command\n");
        commands.Queue("git", 0, "a1b2c3d\n");
        commands.Queue("git", 0, "Add info command\n");
        commands.Queue("git", 0, "git@github.com:suraciii/mohist.git\n");
        commands.Queue("du", 0, "412M\t/repo/.mohist\n");
        commands.Queue("du", 0, "10M\t/repo/.mohist/projects\n");
        commands.Queue("du", 0, "2M\t/repo/.mohist/logs\n");
        commands.Queue("du", 0, "100K\t/repo/.mohist/worktrees\n");

        var pathHandler = new PathAwareHandler();
        pathHandler.Register("/api/projects/proj_1/status",
            HttpStatusCode.OK, """{ "success": true, "data": { "name": "mohist-local", "issues": 96, "activeIssues": 22 } }""");
        pathHandler.Register("/api/projects",
            HttpStatusCode.OK, """{ "success": true, "data": [] }""");
        pathHandler.Register("/api/opencode/runtime",
            HttpStatusCode.OK, """{ "success": true, "data": { "command": "opencode", "models": [] } }""");
        pathHandler.Register("/api/projects/proj_1/agent/status",
            HttpStatusCode.OK, """{ "success": true, "data": { "capacity": { "active": 1, "max": 4 } } }""");

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

        var skillAssetRoot = Path.Combine(repoDir, ".mohist", "cli", "skill-data");
        fs.AddDirectory(Path.Combine(skillAssetRoot, "mohist"));
        fs.AddFile(
            Path.Combine(skillAssetRoot, "mohist", "SKILL.md"),
            "---\nname: mohist\ndescription: Mohist operations\n---\n");

        var collector = new InfoCollector(fs, commands, api, env,
            isSystemdAvailable: () => true, skillAssetService: new SkillAssetService(fs, skillAssetRoot));

        var result = await collector.CollectAsync(verbose: true);

        Assert.NotNull(result.Verbose);
        Assert.NotEmpty(result.Verbose!.Skills.Skills);
        var actualOrigin = result.Verbose.GitRemote.OriginUrl;
        var isRepo = result.Verbose.GitRemote.IsGitRepo;
        var paths = string.Join(",", commands.Invocations.Where(i => i.FileName == "git").Select(i => string.Join(" ", i.Args)));
        Assert.True(isRepo, $"Expected GitRemote.IsGitRepo=true; runner.Source.Path={result.Runner.Source?.Path ?? "null"}; server.Source.Path={result.Server.Source?.Path ?? "null"}; fs.Dirs.Count={fs.Directories.Count}; git-invocations=[{paths}]");
        Assert.Equal("git@github.com:suraciii/mohist.git", actualOrigin);
        Assert.Equal("opencode", result.Verbose.OpencodeRuntime.Command);
        Assert.NotEmpty(result.Verbose.EnvVars);
        Assert.Equal(1, result.Verbose.Capacity.ActiveWorkflows);
        Assert.Equal(3, result.Verbose.DiskUsage.Categories.Count);
    }

    [Fact]
    public async Task Collect_VerboseFalse_LeavesVerboseNull()
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
        commands.Queue("systemctl", 0, "ActiveState=inactive\nMainPID=0\nExecMainStartTimestamp=\nFragmentPath=/units/mohist.service\n");
        commands.Queue("systemctl", 0, "ActiveState=inactive\nMainPID=0\nExecMainStartTimestamp=\nFragmentPath=/units/mohist.service\n");
        commands.Queue("du", 0, "12M\t/repo/.mohist\n");

        var api = BuildApi(fs, commands, HttpStatusCode.OK, """{ "success": true, "data": { "name": "mohist-local", "issues": 0, "activeIssues": 0 } }""");
        var collector = new InfoCollector(fs, commands, api, new MockEnvironmentVariableProvider(), isSystemdAvailable: () => true);

        var result = await collector.CollectAsync(verbose: false);

        Assert.Null(result.Verbose);
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

        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
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
