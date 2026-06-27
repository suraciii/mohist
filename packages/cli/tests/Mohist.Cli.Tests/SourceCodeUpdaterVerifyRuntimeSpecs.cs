using System.Net;
using System.Reflection;
using EnvironmentAbstractions.TestHelpers;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public class SourceCodeUpdaterVerifyRuntimeSpecs
{
    private static SourceCodeUpdater BuildUpdater(
        HttpClient http,
        out StringWriter output,
        out StringWriter error,
        ICommandExecutor? commands = null,
        FakeFileSystem? fs = null)
    {
        output = new StringWriter();
        error = new StringWriter();
        var fileSystem = fs ?? new FakeFileSystem();
        var executor = commands ?? new FakeCommandExecutor();
        var env = new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false);
        var installer = new FakeServiceInstaller();
        var operations = new UpdateOperations(output, error, installer, executor, fileSystem, env, getUserHome: () => "/home/test");
        var validator = new RuntimeConsistencyValidator(http, executor, fileSystem, env, output, getUserHome: () => "/home/test");
        var readiness = new ServiceReadinessProbe(http, output);
        var runnerRefresh = new RunnerRefreshVerifier(http, executor, fileSystem);
        var reporter = new UpdateOutcomeReporter(http, output);
        return new SourceCodeUpdater(output, error, operations, validator, readiness, runnerRefresh, reporter);
    }

    private static RecordingHttpHandler CreateSuccessHandler(string sourceHead, string runnerHash)
    {
        return new RecordingHttpHandler((req, _) =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/api/system/info")
            {
                var body = $"{{\"running\":{{\"gitHash\":\"{sourceHead}\"}},\"services\":{{\"runner\":\"active\"}}}}";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
                });
            }

            if (path == "/")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "<html><head><script src=\"/assets/index-abc.js\"></script></head></html>",
                        System.Text.Encoding.UTF8,
                        "text/html"),
                });
            }

            if (path == "/assets/index-abc.js")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("// bundle"),
                });
            }

            if (path == "/api/runner/identity")
            {
                var body = $"{{\"data\":{{\"buildGitHash\":\"{runnerHash}\"}}}}";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });
    }

    private static UpdateContext BuildContext(StringWriter output, bool dryRun = false, string? sourceHead = null)
    {
        var context = new UpdateContext(dryRun, repoRoot: "/repo", cliPath: "/usr/bin/mo", CancellationToken.None);
        if (sourceHead is not null)
            context.SourceHead = sourceHead;
        return context;
    }

    private static async Task<int> InvokeVerifyRuntimeStageAsync(SourceCodeUpdater updater, UpdateContext context)
    {
        var method = typeof(SourceCodeUpdater).GetMethod(
            "VerifyRuntimeStageAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var task = (Task<int>)method.Invoke(updater, [context, CancellationToken.None])!;
        return await task;
    }

    [Fact]
    public async Task VerifyRuntime_AllChecksPass_IncludesRunnerIdentityLineInOrder()
    {
        var sourceHead = "abc123";
        var handler = CreateSuccessHandler(sourceHead, sourceHead);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:0") };
        var commands = new ScriptedCommandExecutor();
        commands.Queue("/usr/bin/mo", 0, "mo 1.2.3\n");
        var fs = new FakeFileSystem();
        var assetRoot = Path.Combine("/home/test", ".mohist", "cli", "skill-data");
        var skillDir = Path.Combine(assetRoot, "my-skill");
        fs.CreateDirectory(assetRoot);
        fs.CreateDirectory(skillDir);
        fs.AddFile(Path.Combine(skillDir, "SKILL.md"), "# Skill");

        var updater = BuildUpdater(http, out var output, out _, commands, fs);
        var context = BuildContext(output, sourceHead: sourceHead);

        var exitCode = await InvokeVerifyRuntimeStageAsync(updater, context);

        Assert.Equal(0, exitCode);
        Assert.Equal(UpdateOutcome.Ready, context.Outcome);

        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        var connectionIndex = Array.FindIndex(lines, l => l.Contains("Runner connection"));
        var identityIndex = Array.FindIndex(lines, l => l.Contains("Runner identity"));
        var assetsIndex = Array.FindIndex(lines, l => l.Contains("Managed skill assets"));

        Assert.True(connectionIndex >= 0, "Runner connection line should be present");
        Assert.True(identityIndex >= 0, "Runner identity line should be present");
        Assert.True(assetsIndex >= 0, "Managed skill assets line should be present");
        Assert.True(connectionIndex < identityIndex, "Runner identity should follow Runner connection");
        Assert.True(identityIndex < assetsIndex, "Managed skill assets should follow Runner identity");
        Assert.Contains("[ok] Runner identity:", lines[identityIndex]);
    }

    [Fact]
    public async Task VerifyRuntime_RunnerIdentityMismatch_ReportsWarnInOrder()
    {
        var sourceHead = "abc123";
        var handler = CreateSuccessHandler(sourceHead, "def456");
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:0") };
        var commands = new ScriptedCommandExecutor();
        commands.Queue("/usr/bin/mo", 0, "mo 1.2.3\n");
        var fs = new FakeFileSystem();
        var assetRoot = Path.Combine("/home/test", ".mohist", "cli", "skill-data");
        var skillDir = Path.Combine(assetRoot, "my-skill");
        fs.CreateDirectory(assetRoot);
        fs.CreateDirectory(skillDir);
        fs.AddFile(Path.Combine(skillDir, "SKILL.md"), "# Skill");

        var updater = BuildUpdater(http, out var output, out _, commands, fs);
        var context = BuildContext(output, sourceHead: sourceHead);

        var exitCode = await InvokeVerifyRuntimeStageAsync(updater, context);

        Assert.Equal(0, exitCode);
        Assert.Equal(UpdateOutcome.Recovered, context.Outcome);

        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        var connectionIndex = Array.FindIndex(lines, l => l.Contains("Runner connection"));
        var identityIndex = Array.FindIndex(lines, l => l.Contains("Runner identity"));
        var assetsIndex = Array.FindIndex(lines, l => l.Contains("Managed skill assets"));

        Assert.True(connectionIndex < identityIndex, "Runner identity should follow Runner connection");
        Assert.True(identityIndex < assetsIndex, "Managed skill assets should follow Runner identity");
        Assert.Contains("[warn] Runner identity:", lines[identityIndex]);
    }

    [Fact]
    public async Task VerifyRuntime_DryRun_MentionsRunnerIdentity()
    {
        var handler = new RecordingHttpHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:0") };
        var updater = BuildUpdater(http, out var output, out _);
        var context = BuildContext(output, dryRun: true);

        var exitCode = await InvokeVerifyRuntimeStageAsync(updater, context);

        Assert.Equal(0, exitCode);
        Assert.Equal(UpdateOutcome.Ready, context.Outcome);
        Assert.Contains("runner identity", output.ToString(), StringComparison.OrdinalIgnoreCase);
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

    private sealed class FakeServiceInstaller : IServiceInstaller
    {
        public Task<int> InstallServerAsync(ServiceInstallOptions options) => Task.FromResult(0);
        public Task<int> InstallRunnerAsync(ServiceInstallOptions options) => Task.FromResult(0);
        public Task<int> StartServerAsync(ServiceCommandOptions options) => Task.FromResult(0);
        public Task<int> StopServerAsync(ServiceCommandOptions options) => Task.FromResult(0);
        public Task<int> RestartServerAsync(ServiceCommandOptions options) => Task.FromResult(0);
        public Task<int> StatusServerAsync(ServiceCommandOptions options) => Task.FromResult(0);
        public Task<int> LogsServerAsync(ServiceCommandOptions options) => Task.FromResult(0);
        public Task<int> UninstallServerAsync(ServiceCommandOptions options) => Task.FromResult(0);
        public Task<int> StartRunnerAsync(ServiceCommandOptions options) => Task.FromResult(0);
        public Task<int> StopRunnerAsync(ServiceCommandOptions options) => Task.FromResult(0);
        public Task<int> RestartRunnerAsync(ServiceCommandOptions options) => Task.FromResult(0);
        public Task<int> StatusRunnerAsync(ServiceCommandOptions options) => Task.FromResult(0);
        public Task<int> LogsRunnerAsync(ServiceCommandOptions options) => Task.FromResult(0);
        public Task<int> UninstallRunnerAsync(ServiceCommandOptions options) => Task.FromResult(0);
        public Task<bool> IsRunnerRunningAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> IsRunnerInstalledAsync(string? unitDir = null) => Task.FromResult(false);
    }
}
