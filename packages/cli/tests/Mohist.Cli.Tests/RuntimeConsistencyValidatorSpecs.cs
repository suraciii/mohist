using System.Net;
using EnvironmentAbstractions.TestHelpers;
using Microsoft.Extensions.Time.Testing;
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
        TextWriter? output = null,
        TimeProvider? timeProvider = null,
        TimeSpan? runnerIdentityTimeout = null,
        TimeSpan? runnerIdentityPollInterval = null)
    {
        return new RuntimeConsistencyValidator(
            http,
            commands ?? new FakeCommandExecutor(),
            fs ?? new FakeFileSystem(),
            env ?? new MockEnvironmentVariableProvider(),
            output ?? TextWriter.Null,
            getUserHome,
            timeProvider,
            runnerIdentityTimeout,
            runnerIdentityPollInterval);
    }

    private static HttpClient BuildUnusedHttpClient() =>
        new(new RecordingHttpHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));

    private static HttpClient BuildSystemInfoClient(string body) =>
        new(new RecordingHttpHandler((request, _) =>
        {
            Assert.Equal("/api/system/info", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }))
        {
            BaseAddress = new Uri("http://localhost:0"),
        };

    private static RuntimeIdentity ManagedServerIdentity() =>
        new(
            "server",
            "0.0.0+candidate",
            new string('a', 40),
            new string('b', 40),
            new string('c', 64),
            "mohist-server-candidate",
            4);

    private static string ManagedRunningJson(RuntimeIdentity identity, bool includeComponent = true)
    {
        var running = new Dictionary<string, object?>
        {
            ["version"] = identity.Version,
            ["sourceRevision"] = identity.SourceRevision,
            ["gitHash"] = identity.SourceRevision,
            ["treeHash"] = identity.TreeHash,
            ["artifactDigest"] = identity.ArtifactDigest,
            ["releaseId"] = identity.ReleaseId,
            ["generation"] = identity.Generation,
        };
        if (includeComponent)
            running["component"] = identity.Component;
        return System.Text.Json.JsonSerializer.Serialize(new { running });
    }

    private static async Task AssertRequestCountAsync(RecordingHttpHandler handler, int expected)
    {
        await handler.WaitForRequestCountAsync(expected);
        Assert.Equal(expected, handler.Requests.Count);
    }

    [Fact]
    public async Task CheckCliBinaryAsync_VersionReported_ReportsPass()
    {
        var commands = new ScriptedCommandExecutor();
        commands.Queue("/usr/bin/mo", 0, "mo 1.2.3\n");

        var validator = BuildValidator(BuildUnusedHttpClient(), commands);

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
        var validator = BuildValidator(BuildUnusedHttpClient());

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

        var validator = BuildValidator(BuildUnusedHttpClient(), commands);

        var result = await validator.CheckCliBinaryAsync(
            BuildContext(cliPath: "/usr/bin/mo"),
            CancellationToken.None);

        Assert.Equal(RuntimeCheckOutcome.Fail, result.Outcome);
        Assert.Contains("permission denied", result.Message);
    }

    [Fact]
    public async Task VerifyCliRuntimeIdentityAsync_StableLauncherMatchesSourceRevision_ReportsPass()
    {
        var commands = new ScriptedCommandExecutor();
        var sourceRevision = new string('a', 40);
        commands.Queue("/home/test/.local/bin/mo", 0, $"0.0.0+{sourceRevision}\n");
        var validator = BuildValidator(BuildUnusedHttpClient(), commands);
        var expected = new RuntimeIdentity(
            "cli",
            $"0.0.0+{sourceRevision}",
            sourceRevision,
            new string('b', 40),
            new string('c', 64),
            "mohist-cli-candidate",
            1);

        var result = await validator.VerifyCliRuntimeIdentityAsync(
            "/home/test/.local/bin/mo",
            expected,
            CancellationToken.None);

        Assert.Equal("CLI identity", result.Component);
        Assert.Equal(RuntimeCheckOutcome.Pass, result.Outcome);
    }

    [Fact]
    public async Task VerifyCliRuntimeIdentityAsync_StableLauncherReportsOldRevision_Fails()
    {
        var commands = new ScriptedCommandExecutor();
        commands.Queue("/home/test/.local/bin/mo", 0, "0.0.0+oldrevision\n");
        var validator = BuildValidator(BuildUnusedHttpClient(), commands);
        var expected = new RuntimeIdentity(
            "cli",
            "0.0.0+newrevision",
            "newrevision",
            "tree",
            "artifact",
            "release",
            1);

        var result = await validator.VerifyCliRuntimeIdentityAsync(
            "/home/test/.local/bin/mo",
            expected,
            CancellationToken.None);

        Assert.Equal(RuntimeCheckOutcome.Fail, result.Outcome);
        Assert.Contains("expected source revision", result.Message);
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
    public async Task CheckServerIdentityAsync_ServerHashMissing_ReportsFail()
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

        Assert.Equal(RuntimeCheckOutcome.Fail, result.Outcome);
        Assert.Contains("empty git hash", result.Message);
    }

    [Fact]
    public async Task VerifyServerRuntimeIdentityAsync_AllCandidateFieldsMatch_ReportsPass()
    {
        var expected = ManagedServerIdentity();
        var validator = BuildValidator(BuildSystemInfoClient(ManagedRunningJson(expected)));

        var result = await validator.VerifyServerRuntimeIdentityAsync(expected, CancellationToken.None);

        Assert.Equal(RuntimeCheckOutcome.Pass, result.Outcome);
        Assert.Contains(expected.ReleaseId, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyServerRuntimeIdentityAsync_ComponentMissing_ReportsFail()
    {
        var expected = ManagedServerIdentity();
        var validator = BuildValidator(BuildSystemInfoClient(ManagedRunningJson(expected, includeComponent: false)));

        var result = await validator.VerifyServerRuntimeIdentityAsync(expected, CancellationToken.None);

        Assert.Equal(RuntimeCheckOutcome.Fail, result.Outcome);
        Assert.Contains("complete managed runtime identity", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyServerRuntimeIdentityAsync_GenerationDiffers_ReportsFail()
    {
        var expected = ManagedServerIdentity();
        var validator = BuildValidator(BuildSystemInfoClient(ManagedRunningJson(expected with { Generation = 3 })));

        var result = await validator.VerifyServerRuntimeIdentityAsync(expected, CancellationToken.None);

        Assert.Equal(RuntimeCheckOutcome.Fail, result.Outcome);
        Assert.Contains("differs from candidate", result.Message, StringComparison.Ordinal);
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
    public async Task CheckRunnerIdentityAsync_DifferingRunnerHash_ReportsFail()
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

        Assert.Equal(RuntimeCheckOutcome.Fail, result.Outcome);
        Assert.Equal("Runner identity", result.Component);
        Assert.Equal("Runner buildGitHash 'def456' does not match source HEAD 'abc123'", result.Message);
    }

    [Fact]
    public async Task CheckRunnerIdentityAsync_MissingRunnerHash_ReportsFail()
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

        Assert.Equal(RuntimeCheckOutcome.Fail, result.Outcome);
        Assert.Equal("Runner identity", result.Component);
    }

    [Fact]
    public async Task CheckRunnerIdentityAsync_SourceHeadUnavailable_ReportsFail()
    {
        var validator = BuildValidator(BuildUnusedHttpClient());

        var result = await validator.CheckRunnerIdentityAsync(
            BuildContext(repoRoot: "/repo"),
            CancellationToken.None);

        Assert.Equal(RuntimeCheckOutcome.Fail, result.Outcome);
        Assert.Equal("Runner identity", result.Component);
        Assert.Contains("Source HEAD", result.Message);
    }

    [Fact]
    public async Task CheckRunnerIdentityAsync_IdentityImmediatelyAvailable_ReportsPassWithoutDelay()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var handler = new RecordingHttpHandler((req, _) =>
        {
            Assert.Equal("/api/runner/identity", req.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":{\"buildGitHash\":\"abc123\"}}", System.Text.Encoding.UTF8, "application/json"),
            });
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:0") };

        var validator = BuildValidator(
            http,
            timeProvider: time,
            runnerIdentityTimeout: TimeSpan.FromSeconds(30),
            runnerIdentityPollInterval: TimeSpan.FromMilliseconds(500));
        var context = BuildContext(repoRoot: "/repo");
        context.SourceHead = "abc123";

        var result = await validator.CheckRunnerIdentityAsync(context, CancellationToken.None);

        Assert.Equal(RuntimeCheckOutcome.Pass, result.Outcome);
        Assert.Equal("Runner identity matches source HEAD 'abc123'", result.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CheckRunnerIdentityAsync_IdentityArrivesAfterNPolls_ReportsPass()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        const int readyOnAttempt = 3;
        var probeCount = 0;
        var handler = new RecordingHttpHandler((req, _) =>
        {
            Assert.Equal("/api/runner/identity", req.RequestUri!.AbsolutePath);
            var attempt = Interlocked.Increment(ref probeCount);
            if (attempt < readyOnAttempt)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":{\"buildGitHash\":\"abc123\"}}", System.Text.Encoding.UTF8, "application/json"),
            });
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:0") };

        var pollInterval = TimeSpan.FromMilliseconds(500);
        var validator = BuildValidator(
            http,
            timeProvider: time,
            runnerIdentityTimeout: TimeSpan.FromSeconds(30),
            runnerIdentityPollInterval: pollInterval);
        var context = BuildContext(repoRoot: "/repo");
        context.SourceHead = "abc123";

        var checkTask = validator.CheckRunnerIdentityAsync(context, CancellationToken.None);

        for (var i = 0; i < readyOnAttempt - 1; i++)
        {
            await AssertRequestCountAsync(handler, i + 1);
            time.Advance(pollInterval);
        }

        await AssertRequestCountAsync(handler, readyOnAttempt);
        var result = await checkTask;

        Assert.Equal(RuntimeCheckOutcome.Pass, result.Outcome);
        Assert.Equal("Runner identity matches source HEAD 'abc123'", result.Message);
        Assert.Equal(readyOnAttempt, handler.Requests.Count);
        var elapsedSinceStart = time.GetUtcNow() - new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromTicks(pollInterval.Ticks * (readyOnAttempt - 1)), elapsedSinceStart);
    }

    [Fact]
    public async Task CheckRunnerIdentityAsync_IdentityNeverAvailable_ReportsFailAfterTimeout()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var handler = new RecordingHttpHandler((req, _) =>
        {
            Assert.Equal("/api/runner/identity", req.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:0") };

        var timeout = TimeSpan.FromSeconds(2);
        var pollInterval = TimeSpan.FromMilliseconds(500);
        var validator = BuildValidator(
            http,
            timeProvider: time,
            runnerIdentityTimeout: timeout,
            runnerIdentityPollInterval: pollInterval);
        var context = BuildContext(repoRoot: "/repo");
        context.SourceHead = "abc123";

        var checkTask = validator.CheckRunnerIdentityAsync(context, CancellationToken.None);
        await handler.WaitForRequestCountAsync(1);
        Assert.False(checkTask.IsCompleted);

        time.Advance(timeout);
        var result = await checkTask;

        Assert.Equal(RuntimeCheckOutcome.Fail, result.Outcome);
        Assert.Contains("did not respond", result.Message);
        Assert.NotEmpty(handler.Requests);
    }

    [Fact]
    public async Task CheckRunnerIdentityAsync_HangingIdentityRequest_ReportsFailAtTimeout()
    {
        var startedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(startedAt);
        var requestCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new RecordingHttpHandler(async (req, cancellationToken) =>
        {
            Assert.Equal("/api/runner/identity", req.RequestUri!.AbsolutePath);
            using var registration = cancellationToken.Register(static state =>
            {
                ((TaskCompletionSource)state!).SetResult();
            }, requestCanceled);
            await requestCanceled.Task;
            throw new OperationCanceledException(cancellationToken);
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:0") };

        var timeout = TimeSpan.FromSeconds(2);
        var validator = BuildValidator(
            http,
            timeProvider: time,
            runnerIdentityTimeout: timeout,
            runnerIdentityPollInterval: TimeSpan.FromMilliseconds(500));
        var context = BuildContext(repoRoot: "/repo");
        context.SourceHead = "abc123";

        var checkTask = validator.CheckRunnerIdentityAsync(context, CancellationToken.None);
        await AssertRequestCountAsync(handler, 1);
        Assert.False(checkTask.IsCompleted);

        time.Advance(timeout);
        Assert.True(requestCanceled.Task.IsCompleted, "Expected the in-flight request token to be canceled by fake time.");
        var result = await checkTask;

        Assert.Equal(RuntimeCheckOutcome.Fail, result.Outcome);
        Assert.Contains("did not respond", result.Message);
        Assert.Equal(timeout, time.GetUtcNow() - startedAt);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CheckRunnerIdentityAsync_TimeoutShorterThanPollInterval_ReportsFailAtTimeout()
    {
        var startedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(startedAt);
        var handler = new RecordingHttpHandler((req, _) =>
        {
            Assert.Equal("/api/runner/identity", req.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:0") };

        var timeout = TimeSpan.FromSeconds(2);
        var validator = BuildValidator(
            http,
            timeProvider: time,
            runnerIdentityTimeout: timeout,
            runnerIdentityPollInterval: TimeSpan.FromSeconds(5));
        var context = BuildContext(repoRoot: "/repo");
        context.SourceHead = "abc123";

        var checkTask = validator.CheckRunnerIdentityAsync(context, CancellationToken.None);
        await AssertRequestCountAsync(handler, 1);

        time.Advance(timeout);
        var result = await checkTask;

        Assert.Equal(RuntimeCheckOutcome.Fail, result.Outcome);
        Assert.Contains("did not respond", result.Message);
        Assert.Equal(timeout, time.GetUtcNow() - startedAt);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CheckRunnerIdentityAsync_NonDivisibleTimeoutReportsFailAtTimeout()
    {
        var startedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(startedAt);
        var handler = new RecordingHttpHandler((req, _) =>
        {
            Assert.Equal("/api/runner/identity", req.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:0") };

        var timeout = TimeSpan.FromMilliseconds(750);
        var pollInterval = TimeSpan.FromMilliseconds(500);
        var validator = BuildValidator(
            http,
            timeProvider: time,
            runnerIdentityTimeout: timeout,
            runnerIdentityPollInterval: pollInterval);
        var context = BuildContext(repoRoot: "/repo");
        context.SourceHead = "abc123";

        var checkTask = validator.CheckRunnerIdentityAsync(context, CancellationToken.None);
        await AssertRequestCountAsync(handler, 1);

        time.Advance(pollInterval);
        await AssertRequestCountAsync(handler, 2);
        Assert.False(checkTask.IsCompleted);

        time.Advance(timeout - pollInterval);
        var result = await checkTask;

        Assert.Equal(RuntimeCheckOutcome.Fail, result.Outcome);
        Assert.Contains("did not respond", result.Message);
        Assert.Equal(timeout, time.GetUtcNow() - startedAt);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task CheckManagedSkillAssetsAsync_NoAssetRoot_ReportsWarn()
    {
        var fs = new FakeFileSystem();
        var validator = BuildValidator(BuildUnusedHttpClient(), fs: fs, getUserHome: () => "/home/test");

        var result = await validator.CheckManagedSkillAssetsAsync(
            BuildContext(),
            CancellationToken.None);

        Assert.Equal(RuntimeCheckOutcome.Warn, result.Outcome);
        Assert.Contains("missing", result.Message);
        Assert.Contains("mo skill install", result.Message);
        Assert.DoesNotContain("mo skills install", result.Message);
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

        var validator = BuildValidator(BuildUnusedHttpClient(), fs: fs, getUserHome: () => "/home/test");

        var result = await validator.CheckManagedSkillAssetsAsync(
            BuildContext(),
            CancellationToken.None);

        Assert.True(result.Outcome == RuntimeCheckOutcome.Warn,
            $"Expected Warn but got {result.Outcome}: {result.Message}");
        Assert.Contains("contain no skill", result.Message);
        Assert.Contains("mo skill install", result.Message);
        Assert.DoesNotContain("mo skills install", result.Message);
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

        var validator = BuildValidator(BuildUnusedHttpClient(), fs: fs, getUserHome: () => "/home/test");

        var result = await validator.CheckManagedSkillAssetsAsync(
            BuildContext(),
            CancellationToken.None);

        Assert.True(result.Outcome == RuntimeCheckOutcome.Pass,
            $"Expected Pass but got {result.Outcome}: {result.Message}");
        Assert.Contains("Skill assets present", result.Message);
    }
}
