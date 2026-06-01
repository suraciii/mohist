using Xunit;
using Mohist.Cli;
using System.Net;

namespace Mohist.Server.Tests.Specs;

public class UpdateSpecs
{
    [Fact]
    public async Task UpdateAll_UpdatesCliServerAndRunnerWithoutPulling()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        var installer = new SystemdServiceInstaller(
            new StringWriter(),
            new StringWriter(),
            files,
            commands);
        var updater = new SourceCodeUpdater(
            new StringWriter(),
            new StringWriter(),
            installer,
            commands,
            new HttpClient(new SequenceHttpHandler(HttpStatusCode.OK))
            {
                BaseAddress = new Uri("http://localhost:3456"),
            });

        var exitCode = await updater.UpdateAllAsync("/repo", dryRun: false, cliPath: "/home/user/.local/bin/mo");

        Assert.Equal(0, exitCode);
        Assert.Equal("dotnet", commands.ExecutedCommands[0].FileName);
        Assert.Equal("publish", commands.ExecutedCommands[0].Args[0]);
        Assert.Equal("cp", commands.ExecutedCommands[1].FileName);
        Assert.Equal("chmod", commands.ExecutedCommands[2].FileName);
        Assert.Equal("mv", commands.ExecutedCommands[3].FileName);
        Assert.Equal("systemctl", commands.ExecutedCommands[4].FileName);
        Assert.Equal(new[] { "--user", "stop", "mohist-runner.service" }, commands.ExecutedCommands[4].Args);
        Assert.Equal("dotnet", commands.ExecutedCommands[5].FileName);
        Assert.Equal(new[] { "build", "Mohist.sln" }, commands.ExecutedCommands[5].Args);
        Assert.Equal("systemctl", commands.ExecutedCommands[6].FileName);
        Assert.Equal(new[] { "--user", "restart", "mohist.service" }, commands.ExecutedCommands[6].Args);
        Assert.Equal("npm", commands.ExecutedCommands[7].FileName);
        Assert.Equal("systemctl", commands.ExecutedCommands[8].FileName);
        Assert.Equal(new[] { "--user", "restart", "mohist-runner.service" }, commands.ExecutedCommands[8].Args);
        Assert.DoesNotContain(commands.ExecutedCommands, c => c.FileName == "git");
    }

    [Fact]
    public async Task UpdateCli_PublishesAndReplacesResolvedMoBinary()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        commands.SetNextStdout("/home/user/.local/bin/mo\n");
        var installer = new SystemdServiceInstaller(
            new StringWriter(),
            new StringWriter(),
            files,
            commands);
        var updater = new SourceCodeUpdater(
            new StringWriter(),
            new StringWriter(),
            installer,
            commands,
            new HttpClient(new SequenceHttpHandler(HttpStatusCode.OK))
            {
                BaseAddress = new Uri("http://localhost:3456"),
            });

        var exitCode = await updater.UpdateCliAsync("/repo", dryRun: false);

        Assert.Equal(0, exitCode);
        Assert.Equal("sh", commands.ExecutedCommands[0].FileName);
        Assert.Equal(new[] { "-lc", "command -v mo" }, commands.ExecutedCommands[0].Args);
        Assert.Equal("dotnet", commands.ExecutedCommands[1].FileName);
        Assert.Equal("publish", commands.ExecutedCommands[1].Args[0]);
        Assert.Equal("cp", commands.ExecutedCommands[2].FileName);
        Assert.Equal("chmod", commands.ExecutedCommands[3].FileName);
        Assert.Equal("mv", commands.ExecutedCommands[4].FileName);
        Assert.Equal("/home/user/.local/bin/mo", commands.ExecutedCommands[4].Args[1]);
    }

    [Fact]
    public async Task UpdateServer_BuildsCurrentSourceAndRestarts()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        var installer = new SystemdServiceInstaller(
            new StringWriter(),
            new StringWriter(),
            files,
            commands);
        var updater = new SourceCodeUpdater(
            new StringWriter(),
            new StringWriter(),
            installer,
            commands,
            new HttpClient(new SequenceHttpHandler(HttpStatusCode.OK))
            {
                BaseAddress = new Uri("http://localhost:3456"),
            });

        var exitCode = await updater.UpdateServerAsync("/repo", dryRun: false);

        Assert.Equal(0, exitCode);
        Assert.Equal(2, commands.ExecutedCommands.Count);
        Assert.Equal("dotnet", commands.ExecutedCommands[0].FileName);
        Assert.Equal(new[] { "build", "Mohist.sln" }, commands.ExecutedCommands[0].Args);
        Assert.Equal("/repo", commands.ExecutedCommands[0].WorkingDirectory);
        Assert.Equal("systemctl", commands.ExecutedCommands[1].FileName);
        Assert.Equal(new[] { "--user", "restart", "mohist.service" }, commands.ExecutedCommands[1].Args);
    }

    [Fact]
    public async Task UpdateServer_WaitsForReadinessAfterRestart()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        var readiness = new SequenceHttpHandler(
            null,
            new ResponseSpec(HttpStatusCode.OK),
            new ResponseSpec(HttpStatusCode.OK, "<html><script src=\"/assets/app.js\"></script></html>", "text/html"),
            new ResponseSpec(HttpStatusCode.OK));
        var stdout = new StringWriter();
        var installer = new SystemdServiceInstaller(
            stdout,
            new StringWriter(),
            files,
            commands);
        var updater = new SourceCodeUpdater(
            stdout,
            new StringWriter(),
            installer,
            commands,
            new HttpClient(readiness)
            {
                BaseAddress = new Uri("http://localhost:3456"),
            },
            TimeSpan.FromSeconds(1));

        var exitCode = await updater.UpdateServerAsync("/repo", dryRun: false);

        Assert.Equal(0, exitCode);
        Assert.Equal(4, readiness.Requests);
        Assert.Equal(["/api/health", "/api/health", "/", "/assets/app.js"], readiness.Paths);
        Assert.Contains("Server is ready.", stdout.ToString());
    }

    [Fact]
    public async Task UpdateServer_WhenReadinessDoesNotBecomeReady_ReturnsFailure()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        var stderr = new StringWriter();
        var installer = new SystemdServiceInstaller(
            new StringWriter(),
            stderr,
            files,
            commands);
        var updater = new SourceCodeUpdater(
            new StringWriter(),
            stderr,
            installer,
            commands,
            new HttpClient(new SequenceHttpHandler(
                new ResponseSpec(HttpStatusCode.OK),
                new ResponseSpec(HttpStatusCode.InternalServerError)))
            {
                BaseAddress = new Uri("http://localhost:3456"),
            },
            TimeSpan.FromMilliseconds(20));

        var exitCode = await updater.UpdateServerAsync("/repo", dryRun: false);

        Assert.Equal(1, exitCode);
        Assert.Contains("Mohist readiness checks did not pass", stderr.ToString());
        Assert.Contains("Last readiness error: GET / returned 500 InternalServerError", stderr.ToString());
    }

    [Fact]
    public async Task UpdateServer_ReadinessChecksAssetHeadersWithoutReadingBundleBody()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        var readiness = new SequenceHttpHandler(
            new ResponseSpec(HttpStatusCode.OK),
            new ResponseSpec(HttpStatusCode.OK, "<html><script src=\"/assets/app.js\"></script></html>", "text/html"),
            new ResponseSpec(HttpStatusCode.OK, Content: new NeverCompletingContent()));
        var installer = new SystemdServiceInstaller(
            new StringWriter(),
            new StringWriter(),
            files,
            commands);
        var updater = new SourceCodeUpdater(
            new StringWriter(),
            new StringWriter(),
            installer,
            commands,
            new HttpClient(readiness)
            {
                BaseAddress = new Uri("http://localhost:3456"),
            },
            TimeSpan.FromSeconds(1));

        var exitCode = await updater.UpdateServerAsync("/repo", dryRun: false);

        Assert.Equal(0, exitCode);
        Assert.Equal(["/api/health", "/", "/assets/app.js"], readiness.Paths);
    }

    [Fact]
    public void SourceCodeUpdater_DefaultsServerReadinessToIpv4Loopback()
    {
        var previous = Environment.GetEnvironmentVariable("MOHIST_SERVER_URL");
        try
        {
            Environment.SetEnvironmentVariable("MOHIST_SERVER_URL", null);
            var files = new FakeFileSystem();
            var commands = new FakeCommandExecutor();
            var installer = new SystemdServiceInstaller(
                new StringWriter(),
                new StringWriter(),
                files,
                commands);
            var updater = new SourceCodeUpdater(
                new StringWriter(),
                new StringWriter(),
                installer,
                commands);

            Assert.Equal(new Uri("http://127.0.0.1:3456"), updater.ServerBaseAddress);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MOHIST_SERVER_URL", previous);
        }
    }

    [Fact]
    public async Task UpdateRunner_BuildsCurrentSourceAndRestarts()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        var installer = new SystemdServiceInstaller(
            new StringWriter(),
            new StringWriter(),
            files,
            commands);
        var updater = new SourceCodeUpdater(
            new StringWriter(),
            new StringWriter(),
            installer,
            commands);

        var exitCode = await updater.UpdateRunnerAsync("/repo", dryRun: false);

        Assert.Equal(0, exitCode);
        Assert.Equal(2, commands.ExecutedCommands.Count);
        Assert.Equal("npm", commands.ExecutedCommands[0].FileName);
        Assert.Equal(new[] { "run", "build", "-w", "packages/runner" }, commands.ExecutedCommands[0].Args);
        Assert.Equal("systemctl", commands.ExecutedCommands[1].FileName);
        Assert.Equal(new[] { "--user", "restart", "mohist-runner.service" }, commands.ExecutedCommands[1].Args);
    }

    [Fact]
    public async Task UpdateAll_WhenServerUpdateFailsAfterStoppingRunner_RestoresRunner()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        commands.SetExitCodeFor("dotnet", args => args.Length > 0 && args[0] == "build", 1);
        var stderr = new StringWriter();
        var installer = new SystemdServiceInstaller(
            new StringWriter(),
            stderr,
            files,
            commands);
        var updater = new SourceCodeUpdater(
            new StringWriter(),
            stderr,
            installer,
            commands,
            new HttpClient(new SequenceHttpHandler(HttpStatusCode.OK))
            {
                BaseAddress = new Uri("http://localhost:3456"),
            });

        var exitCode = await updater.UpdateAllAsync("/repo", dryRun: false, cliPath: "/home/user/.local/bin/mo");

        Assert.Equal(1, exitCode);
        Assert.Contains(commands.ExecutedCommands, c =>
            c.FileName == "systemctl" && c.Args.SequenceEqual(["--user", "stop", "mohist-runner.service"]));
        Assert.Contains(commands.ExecutedCommands, c =>
            c.FileName == "systemctl" && c.Args.SequenceEqual(["--user", "start", "mohist-runner.service"]));
        Assert.Contains("Restoring runner service", stderr.ToString());
    }

    [Fact]
    public async Task UpdateAll_WhenRunnerBuildFailsAfterServerReady_RestoresRunner()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        commands.SetExitCodeFor("npm", args => args.SequenceEqual(["run", "build", "-w", "packages/runner"]), 1);
        var stderr = new StringWriter();
        var installer = new SystemdServiceInstaller(
            new StringWriter(),
            stderr,
            files,
            commands);
        var updater = new SourceCodeUpdater(
            new StringWriter(),
            stderr,
            installer,
            commands,
            new HttpClient(new SequenceHttpHandler(HttpStatusCode.OK))
            {
                BaseAddress = new Uri("http://localhost:3456"),
            });

        var exitCode = await updater.UpdateAllAsync("/repo", dryRun: false, cliPath: "/home/user/.local/bin/mo");

        Assert.Equal(1, exitCode);
        Assert.Contains(commands.ExecutedCommands, c =>
            c.FileName == "systemctl" && c.Args.SequenceEqual(["--user", "restart", "mohist.service"]));
        Assert.Contains(commands.ExecutedCommands, c =>
            c.FileName == "systemctl" && c.Args.SequenceEqual(["--user", "start", "mohist-runner.service"]));
        Assert.Contains("Restoring runner service", stderr.ToString());
    }

    [Fact]
    public async Task UpdateServer_WhenBuildFails_AbortsWithError()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        commands.SetNextExitCode(1);  // build fails
        var stderr = new StringWriter();
        var installer = new SystemdServiceInstaller(
            new StringWriter(),
            stderr,
            files,
            commands);
        var updater = new SourceCodeUpdater(
            new StringWriter(),
            stderr,
            installer,
            commands);

        var exitCode = await updater.UpdateServerAsync("/repo", dryRun: false);

        Assert.Equal(1, exitCode);
        Assert.Single(commands.ExecutedCommands);
        Assert.Contains("Build failed", stderr.ToString());
    }

    [Fact]
    public async Task UpdateServer_WhenBuildFails_PrintsCommandOutput()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        commands.SetNextResult(1, "npm error EBADPLATFORM", "MSB3073");
        var stderr = new StringWriter();
        var installer = new SystemdServiceInstaller(
            new StringWriter(),
            stderr,
            files,
            commands);
        var updater = new SourceCodeUpdater(
            new StringWriter(),
            stderr,
            installer,
            commands);

        var exitCode = await updater.UpdateServerAsync("/repo", dryRun: false);

        Assert.Equal(1, exitCode);
        var output = stderr.ToString();
        Assert.Contains("npm error EBADPLATFORM", output);
        Assert.Contains("MSB3073", output);
        Assert.Contains("Build failed", output);
    }

    [Fact]
    public async Task UpdateCli_WhenPublishFails_PrintsCommandOutput()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        commands.SetNextResult(1, "publish stdout", "publish stderr");
        var stderr = new StringWriter();
        var installer = new SystemdServiceInstaller(
            new StringWriter(),
            stderr,
            files,
            commands);
        var updater = new SourceCodeUpdater(
            new StringWriter(),
            stderr,
            installer,
            commands);

        var exitCode = await updater.UpdateCliAsync("/repo", dryRun: false, cliPath: "/home/user/.local/bin/mo");

        Assert.Equal(1, exitCode);
        var output = stderr.ToString();
        Assert.Contains("publish stdout", output);
        Assert.Contains("publish stderr", output);
        Assert.Contains("CLI publish failed", output);
    }

    [Fact]
    public async Task UpdateServer_InDryRunMode_PreviewsCommands()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        var stdout = new StringWriter();
        var installer = new SystemdServiceInstaller(
            stdout,
            new StringWriter(),
            files,
            commands);
        var updater = new SourceCodeUpdater(
            stdout,
            new StringWriter(),
            installer,
            commands);

        var exitCode = await updater.UpdateServerAsync("/repo", dryRun: true);

        Assert.Equal(0, exitCode);
        Assert.Empty(commands.ExecutedCommands);
        var output = stdout.ToString();
        Assert.Contains("Dry run: would execute:", output);
        Assert.DoesNotContain("git pull", output);
        Assert.Contains("dotnet build Mohist.sln", output);
        Assert.Contains("wait for /api/health, /, and referenced /assets/* response headers readiness checks", output);
    }

    private sealed class FakeFileSystem : IFileSystem
    {
        private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);

        public Task WriteAllTextAsync(string path, string contents)
        {
            _files[Path.GetFullPath(path)] = contents;
            return Task.CompletedTask;
        }

        public Task<string> ReadAllTextAsync(string path) => Task.FromResult(Read(path));

        public bool Exists(string path) => _files.ContainsKey(Path.GetFullPath(path));

        public bool DirectoryExists(string path) => false;

        public void CreateDirectory(string path)
        {
        }

        public IEnumerable<string> EnumerateFiles(string path, string searchPattern, SearchOption searchOption) => [];

        public void Delete(string path) => _files.Remove(Path.GetFullPath(path));

        public string Read(string path) => _files[Path.GetFullPath(path)];
    }

    private sealed class FakeCommandExecutor : ICommandExecutor
    {
        public readonly List<(string FileName, string[] Args, string? WorkingDirectory)> ExecutedCommands = new();
        private readonly Queue<int> _exitCodes = new();
        private readonly Queue<string> _stdout = new();
        private readonly Queue<string> _stderr = new();
        private readonly List<(string FileName, Func<string[], bool> Match, int ExitCode)> _exitCodeRules = new();

        public void SetNextExitCode(int code) => _exitCodes.Enqueue(code);
        public void SetNextStdout(string stdout) => _stdout.Enqueue(stdout);
        public void SetNextResult(int exitCode, string stdout, string stderr)
        {
            _exitCodes.Enqueue(exitCode);
            _stdout.Enqueue(stdout);
            _stderr.Enqueue(stderr);
        }
        public void SetExitCodeFor(string fileName, Func<string[], bool> match, int code) => _exitCodeRules.Add((fileName, match, code));

        public Task<(int ExitCode, string Stdout, string Stderr)> ExecuteAsync(
            string fileName, string[] args, string? workingDirectory = null)
        {
            ExecutedCommands.Add((fileName, args, workingDirectory));
            var rule = _exitCodeRules.FirstOrDefault(rule => rule.FileName == fileName && rule.Match(args));
            var code = rule.Match is not null ? rule.ExitCode : _exitCodes.Count > 0 ? _exitCodes.Dequeue() : 0;
            var stdout = _stdout.Count > 0 ? _stdout.Dequeue() : "";
            var stderr = _stderr.Count > 0 ? _stderr.Dequeue() : "";
            return Task.FromResult((code, stdout, stderr));
        }
    }

    private sealed class SequenceHttpHandler : HttpMessageHandler
    {
        private readonly ResponseSpec?[] _responses;

        public int Requests { get; private set; }
        public List<string> Paths { get; } = new();

        public SequenceHttpHandler(params HttpStatusCode?[] statuses)
            : this(ExpandStatusResponses(statuses))
        {
        }

        public SequenceHttpHandler(params ResponseSpec?[] responses)
        {
            _responses = responses.Length == 0 ? [new ResponseSpec(HttpStatusCode.OK)] : responses;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri?.PathAndQuery ?? "");
            var index = Math.Min(Requests, _responses.Length - 1);
            Requests++;
            var response = _responses[index];
            if (response is null)
                throw new HttpRequestException("server not ready");

            var message = new HttpResponseMessage(response.StatusCode);
            if (response.Body is not null)
            {
                message.Content = new StringContent(response.Body);
                if (response.ContentType is not null)
                    message.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(response.ContentType);
            }
            else if (response.Content is not null)
            {
                message.Content = response.Content;
                if (response.ContentType is not null)
                    message.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(response.ContentType);
            }

            return Task.FromResult(message);
        }
    }

    private sealed record ResponseSpec(
        HttpStatusCode StatusCode,
        string? Body = null,
        string? ContentType = null,
        HttpContent? Content = null);

    private sealed class NeverCompletingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => Task.Delay(Timeout.InfiniteTimeSpan);

        protected override bool TryComputeLength(out long length)
        {
            length = 1024 * 1024;
            return true;
        }
    }

    private static ResponseSpec?[] ExpandStatusResponses(HttpStatusCode?[] statuses)
    {
        if (statuses.Length == 0)
            statuses = [HttpStatusCode.OK];

        var expanded = new List<ResponseSpec?>();
        foreach (var response in statuses)
        {
            if (response is null)
            {
                expanded.Add(null);
                continue;
            }

            if (response.Value == HttpStatusCode.OK)
            {
                expanded.Add(new ResponseSpec(HttpStatusCode.OK));
                expanded.Add(new ResponseSpec(HttpStatusCode.OK, "<html><script src=\"/assets/app.js\"></script></html>", "text/html"));
                expanded.Add(new ResponseSpec(HttpStatusCode.OK));
                continue;
            }

            expanded.Add(new ResponseSpec(response.Value));
        }

        return expanded.ToArray();
    }
}
