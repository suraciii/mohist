using System.Net;
using EnvironmentAbstractions.TestHelpers;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public class RuntimeConsistencyValidatorSpecs
{
    private static UpdateContext BuildContext(string? repoRoot = null, string? cliPath = null)
        => new(dryRun: false, repoRoot: repoRoot, cliPath: cliPath, cancellationToken: CancellationToken.None);

    private static RuntimeConsistencyValidator BuildValidator(
        HttpClient http,
        ICommandExecutor? commands = null,
        FakeFileSystem? fs = null,
        MockEnvironmentVariableProvider? env = null,
        Func<string?>? getUserHome = null,
        TextWriter? output = null)
    {
        return new RuntimeConsistencyValidator(
            http,
            commands ?? new FakeCommandExecutor(),
            fs ?? new FakeFileSystem(),
            env ?? new MockEnvironmentVariableProvider(),
            output ?? TextWriter.Null,
            getUserHome);
    }

    [Fact]
    public async Task CheckCliBinaryAsync_VersionReported_ReportsPass()
    {
        var commands = new ScriptedCommandExecutor();
        commands.Queue("/usr/bin/mo", 0, "mo 1.2.3\n");

        var validator = BuildValidator(new HttpClient(), commands);

        var result = await validator.CheckCliBinaryAsync(
            BuildContext(cliPath: "/usr/bin/mo"),
            CancellationToken.None);

        Assert.Equal("CLI binary", result.Component);
        Assert.Equal(RuntimeCheckOutcome.Pass, result.Outcome);
        Assert.Contains("mo 1.2.3", result.Message);
    }

    [Fact]
    public async Task CheckCliBinaryAsync_NoCliPath_ReportsFail()
    {
        var validator = BuildValidator(new HttpClient());

        var result = await validator.CheckCliBinaryAsync(
            BuildContext(cliPath: null),
            CancellationToken.None);

        Assert.Equal(RuntimeCheckOutcome.Fail, result.Outcome);
        Assert.Contains("CLI binary path was not resolved", result.Message);
    }

    [Fact]
    public async Task CheckCliBinaryAsync_NonZeroExit_ReportsFail()
    {
        var commands = new ScriptedCommandExecutor();
        commands.Queue("/usr/bin/mo", 1, stdout: "", stderr: "permission denied");

        var validator = BuildValidator(new HttpClient(), commands);

        var result = await validator.CheckCliBinaryAsync(
            BuildContext(cliPath: "/usr/bin/mo"),
            CancellationToken.None);

        Assert.Equal(RuntimeCheckOutcome.Fail, result.Outcome);
        Assert.Contains("permission denied", result.Message);
    }

    [Fact]
    public async Task CheckServerIdentityAsync_ServerHashMatchesSourceHead_ReportsPass()
    {
        var handler = new RecordingHttpHandler((req, _) =>
        {
            Assert.Equal("/api/system/info", req.RequestUri!.AbsolutePath);
            var respBody = "{\"running\":{\"gitHash\":\"abc123\"}}";
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(respBody, System.Text.Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:0") };

        var commands = new ScriptedCommandExecutor();
        var validator = BuildValidator(http, commands);

        var context = BuildContext(repoRoot: "/repo");
        context.SourceHead = "abc123";

        var result = await validator.CheckServerIdentityAsync(context, CancellationToken.None);

        Assert.Equal(RuntimeCheckOutcome.Pass, result.Outcome);
        Assert.Contains("abc123", result.Message);
    }

    [Fact]
    public async Task CheckServerIdentityAsync_ServerHashMissing_ReportsWarn()
    {
        var handler = new RecordingHttpHandler((req, _) =>
        {
            var respBody = "{\"running\":{}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(respBody, System.Text.Encoding.UTF8, "application/json"),
            });
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:0") };

        var validator = BuildValidator(http);

        var result = await validator.CheckServerIdentityAsync(
            BuildContext(repoRoot: "/repo"),
            CancellationToken.None);

        Assert.Equal(RuntimeCheckOutcome.Warn, result.Outcome);
        Assert.Contains("empty git hash", result.Message);
    }

    [Fact]
    public async Task CheckWebAssetsAsync_RootHtmlWithAssetBundle_ReportsPass()
    {
        var handler = new RecordingHttpHandler((req, _) =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/" || path == "")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "<html><head><script src=\"/assets/index-abc.js\"></script></head></html>",
                        System.Text.Encoding.UTF8,
                        "text/html"),
                });
            }
            if (path.StartsWith("/assets/"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("// bundle content"),
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:0") };

        var validator = BuildValidator(http);

        var result = await validator.CheckWebAssetsAsync(
            BuildContext(),
            CancellationToken.None);

        Assert.Equal(RuntimeCheckOutcome.Pass, result.Outcome);
        Assert.Contains("/assets/", result.Message);
    }

    [Fact]
    public async Task CheckWebAssetsAsync_RootReturnsNonHtml_ReportsFail()
    {
        var handler = new RecordingHttpHandler((req, _) =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("not html"),
            });
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:0") };

        var validator = BuildValidator(http);

        var result = await validator.CheckWebAssetsAsync(
            BuildContext(),
            CancellationToken.None);

        Assert.Equal(RuntimeCheckOutcome.Fail, result.Outcome);
        Assert.Contains("text/html", result.Message);
    }

    [Fact]
    public async Task CheckRunnerConnectionAsync_RunnerActive_ReportsPass()
    {
        var handler = new RecordingHttpHandler((req, _) =>
        {
            var respBody = "{\"services\":{\"runner\":\"active\"}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(respBody, System.Text.Encoding.UTF8, "application/json"),
            });
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:0") };

        var validator = BuildValidator(http);

        var result = await validator.CheckRunnerConnectionAsync(
            BuildContext(),
            CancellationToken.None);

        Assert.Equal(RuntimeCheckOutcome.Pass, result.Outcome);
        Assert.Contains("active", result.Message);
    }

    [Fact]
    public async Task CheckRunnerConnectionAsync_RunnerInactive_ReportsFail()
    {
        var handler = new RecordingHttpHandler((req, _) =>
        {
            var respBody = "{\"services\":{\"runner\":\"inactive\"}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(respBody, System.Text.Encoding.UTF8, "application/json"),
            });
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:0") };

        var validator = BuildValidator(http);

        var result = await validator.CheckRunnerConnectionAsync(
            BuildContext(),
            CancellationToken.None);

        Assert.Equal(RuntimeCheckOutcome.Fail, result.Outcome);
        Assert.Contains("inactive", result.Message);
    }

    [Fact]
    public async Task CheckRunnerIdentityAsync_MatchingRunnerHash_ReportsPass()
    {
        var handler = new RecordingHttpHandler((req, _) =>
        {
            Assert.Equal("/api/runner/identity", req.RequestUri!.AbsolutePath);
            var respBody = "{\"data\":{\"buildGitHash\":\"abc123\"}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(respBody, System.Text.Encoding.UTF8, "application/json"),
            });
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:0") };

        var validator = BuildValidator(http);
        var context = BuildContext(repoRoot: "/repo");
        context.SourceHead = "abc123";

        var result = await validator.CheckRunnerIdentityAsync(context, CancellationToken.None);

        Assert.Equal(RuntimeCheckOutcome.Pass, result.Outcome);
        Assert.Equal("Runner identity", result.Component);
        Assert.Equal("Runner identity matches source HEAD 'abc123'", result.Message);
    }

    [Fact]
    public async Task CheckRunnerIdentityAsync_DifferingRunnerHash_ReportsWarn()
    {
        var handler = new RecordingHttpHandler((req, _) =>
        {
            Assert.Equal("/api/runner/identity", req.RequestUri!.AbsolutePath);
            var respBody = "{\"data\":{\"buildGitHash\":\"def456\"}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(respBody, System.Text.Encoding.UTF8, "application/json"),
            });
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:0") };

        var validator = BuildValidator(http);
        var context = BuildContext(repoRoot: "/repo");
        context.SourceHead = "abc123";

        var result = await validator.CheckRunnerIdentityAsync(context, CancellationToken.None);

        Assert.Equal(RuntimeCheckOutcome.Warn, result.Outcome);
        Assert.Equal("Runner identity", result.Component);
        Assert.Equal("Runner buildGitHash 'def456' does not match source HEAD 'abc123'", result.Message);
    }

    [Fact]
    public async Task CheckRunnerIdentityAsync_MissingRunnerHash_ReportsWarn()
    {
        var handler = new RecordingHttpHandler((req, _) =>
        {
            Assert.Equal("/api/runner/identity", req.RequestUri!.AbsolutePath);
            var respBody = "{\"data\":{\"buildGitHash\":null}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(respBody, System.Text.Encoding.UTF8, "application/json"),
            });
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:0") };

        var validator = BuildValidator(http);
        var context = BuildContext(repoRoot: "/repo");
        context.SourceHead = "abc123";

        var result = await validator.CheckRunnerIdentityAsync(context, CancellationToken.None);

        Assert.Equal(RuntimeCheckOutcome.Warn, result.Outcome);
        Assert.Equal("Runner identity", result.Component);
    }

    [Fact]
    public async Task CheckRunnerIdentityAsync_SourceHeadUnavailable_ReportsWarn()
    {
        var validator = BuildValidator(new HttpClient());

        var result = await validator.CheckRunnerIdentityAsync(
            BuildContext(repoRoot: "/repo"),
            CancellationToken.None);

        Assert.Equal(RuntimeCheckOutcome.Warn, result.Outcome);
        Assert.Equal("Runner identity", result.Component);
        Assert.Contains("Source HEAD", result.Message);
    }

    [Fact]
    public async Task CheckRunnerIdentityAsync_EndpointUnreachable_ReportsWarn()
    {
        var handler = new RecordingHttpHandler((req, _) =>
        {
            Assert.Equal("/api/runner/identity", req.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:0") };

        var validator = BuildValidator(http);
        var context = BuildContext(repoRoot: "/repo");
        context.SourceHead = "abc123";

        var result = await validator.CheckRunnerIdentityAsync(context, CancellationToken.None);

        Assert.Equal(RuntimeCheckOutcome.Warn, result.Outcome);
        Assert.Equal("Runner identity", result.Component);
    }

    [Fact]
    public async Task CheckManagedSkillAssetsAsync_NoAssetRoot_ReportsWarn()
    {
        var fs = new FakeFileSystem();
        var validator = BuildValidator(new HttpClient(), fs: fs, getUserHome: () => "/home/test");

        var result = await validator.CheckManagedSkillAssetsAsync(
            BuildContext(),
            CancellationToken.None);

        Assert.Equal(RuntimeCheckOutcome.Warn, result.Outcome);
        Assert.Contains("missing", result.Message);
    }

    [Fact]
    public async Task CheckManagedSkillAssetsAsync_AssetRootWithoutSkills_ReportsWarn()
    {
        var fs = new FakeFileSystem();
        var assetRoot = Path.Combine("/home/test", ".mohist", "cli", "skill-data");
        var skillDir = Path.Combine(assetRoot, "no-skill-yet");
        fs.CreateDirectory(assetRoot);
        fs.CreateDirectory(skillDir);
        fs.AddFile(Path.Combine(skillDir, "README.md"), "not a skill");

        var validator = BuildValidator(new HttpClient(), fs: fs, getUserHome: () => "/home/test");

        var result = await validator.CheckManagedSkillAssetsAsync(
            BuildContext(),
            CancellationToken.None);

        Assert.True(result.Outcome == RuntimeCheckOutcome.Warn,
            $"Expected Warn but got {result.Outcome}: {result.Message}");
        Assert.Contains("contain no skill", result.Message);
    }

    [Fact]
    public async Task CheckManagedSkillAssetsAsync_AssetRootWithSkill_ReportsPass()
    {
        var fs = new FakeFileSystem();
        var assetRoot = Path.Combine("/home/test", ".mohist", "cli", "skill-data");
        var skillDir = Path.Combine(assetRoot, "my-skill");
        fs.CreateDirectory(assetRoot);
        fs.CreateDirectory(skillDir);
        fs.AddFile(Path.Combine(skillDir, "SKILL.md"), "# My Skill");

        var validator = BuildValidator(new HttpClient(), fs: fs, getUserHome: () => "/home/test");

        var result = await validator.CheckManagedSkillAssetsAsync(
            BuildContext(),
            CancellationToken.None);

        Assert.True(result.Outcome == RuntimeCheckOutcome.Pass,
            $"Expected Pass but got {result.Outcome}: {result.Message}");
        Assert.Contains("Skill assets present", result.Message);
    }

    private sealed class ScriptedCommandExecutor : ICommandExecutor
    {
        private readonly Dictionary<string, Queue<(int ExitCode, string Stdout, string Stderr)>> _byFileName = new(StringComparer.Ordinal);

        public void Queue(string fileName, int exitCode, string stdout = "", string stderr = "")
        {
            if (!_byFileName.TryGetValue(fileName, out var bucket))
            {
                bucket = new Queue<(int, string, string)>();
                _byFileName[fileName] = bucket;
            }
            bucket.Enqueue((exitCode, stdout, stderr));
        }

        public Task<(int ExitCode, string Stdout, string Stderr)> ExecuteAsync(
            string fileName, string[] args, string? workingDirectory = null, CancellationToken cancellationToken = default)
        {
            if (_byFileName.TryGetValue(fileName, out var bucket) && bucket.Count > 0)
                return Task.FromResult(bucket.Dequeue());
            return Task.FromResult((0, string.Empty, string.Empty));
        }
    }
}
