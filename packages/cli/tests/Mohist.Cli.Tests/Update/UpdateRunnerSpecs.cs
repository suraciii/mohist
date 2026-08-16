using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Mohist.Cli;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests.Update;

public class UpdateRunnerSpecs
{
    private static string ReadUpdateInterruptId(HttpRequestMessage request)
    {
        var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
        using var document = JsonDocument.Parse(body ?? throw new InvalidOperationException(
            "update interrupt request body is required"));
        var updateInterruptId = document.RootElement.GetProperty("updateInterruptId").GetString();
        Assert.True(Guid.TryParse(updateInterruptId, out _));
        return updateInterruptId!;
    }

    private static HttpResponseMessage InterruptResponse(
        HttpRequestMessage request,
        string runnerId,
        string[] interruptedWorkIds)
    {
        var updateInterruptId = ReadUpdateInterruptId(request);
        return RecordingHttpHandler.Json(new
        {
            success = true,
            data = new
            {
                runnerId,
                status = "interrupted",
                updateInterruptId,
                interruptedWorkIds,
                interruptedWorkCount = interruptedWorkIds.Length,
            },
        });
    }

    private static HttpResponseMessage CancelResponse(string runnerId, string updateInterruptId) =>
        RecordingHttpHandler.Json(new
        {
            success = true,
            data = new
            {
                runnerId,
                updateInterruptId,
                status = "cancelled",
            },
        });

    private static RecordingHttpHandler CreateIdentityThenNoReconnectHandler(string hash)
    {
        var identityRequests = 0;
        return new RecordingHttpHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path == "/api/runner/runner-1/update-interrupt")
            {
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new
                    {
                        runnerId = "runner-1",
                        status = "interrupted",
                        interruptedWorkIds = Array.Empty<string>(),
                        interruptedWorkCount = 0,
                    },
                }));
            }

            if (request.Method == HttpMethod.Get && path == "/api/runner/identity"
                && Interlocked.Increment(ref identityRequests) == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        UpdateTestFactory.BuildRunnerIdentityResponse("runner-1", "test-host", hash, "online"),
                        System.Text.Encoding.UTF8,
                        "application/json"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });
    }

    [Fact]
    public async Task UpdateRunner_InterruptsRunnerByIdentityBeforeRestart()
    {
        var f = new UpdateTestFactory();
        var installer = new FakeServiceInstaller { RunnerInstalled = true };
        var hash = "abcdef1234567890abcdef1234567890abcdef12";
        f.Commands.SetResultFor("git", args => args.SequenceEqual(["rev-parse", "HEAD"]), 0, hash + "\n", "");
        var handler = new RecordingHttpHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/api/runner/identity")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        UpdateTestFactory.BuildRunnerIdentityResponse("runner-1", "test-host", hash, "online"),
                        System.Text.Encoding.UTF8,
                        "application/json"),
                });
            }

            if (request.Method == HttpMethod.Get
                && Uri.UnescapeDataString(path) == "/api/runner/runner-1/update-operation/runner-update:runner/recovery-status")
            {
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new
                    {
                        operationId = "runner-update:runner",
                        runnerId = "runner-1",
                        operationStatus = "settled",
                        complete = true,
                        affectedWorks = new[]
                        {
                            new { ownerKind = "agent-job", ownerId = "job-1", workId = "job-1", taskRunId = (string?)null, workType = "agent-job", status = "receipt-acked", acknowledged = true },
                            new { ownerKind = "agent-job", ownerId = "job-2", workId = "job-2", taskRunId = (string?)null, workType = "agent-job", status = "replacement-settled", acknowledged = true },
                        },
                    },
                }));
            }

            if (request.Method == HttpMethod.Post && path == "/api/runner/runner-1/update-interrupt")
            {
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new
                    {
                        runnerId = "runner-1",
                        status = "interrupted",
                        interruptedWorkIds = new[] { "job-1", "job-2" },
                        interruptedWorkCount = 2,
                        operationId = "runner-update:runner",
                        affectedWorks = new[]
                        {
                            new { ownerKind = "agent-job", ownerId = "job-1", workId = "job-1", taskRunId = (string?)null, workType = "agent-job" },
                            new { ownerKind = "agent-job", ownerId = "job-2", workId = "job-2", taskRunId = (string?)null, workType = "agent-job" },
                        },
                    },
                }));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });
        var updater = f.BuildUpdater(
            handler,
            unitDir: UpdateTestFactory.UnitDir,
            getLocalHostname: () => "test-host",
            serviceInstaller: installer);

        var exitCode = await updater.UpdateRunnerAsync("/repo", dryRun: false);

        Assert.Equal(0, exitCode);
        Assert.Equal(
            new[]
            {
                "/api/runner/identity",
                "/api/runner/runner-1/update-interrupt",
                "/api/runner/identity",
                "/api/runner/runner-1/update-operation/runner-update:runner/recovery-status",
            },
            handler.Requests.Select(request => Uri.UnescapeDataString(request.RequestUri!.AbsolutePath)));
        Assert.Contains(nameof(FakeServiceInstaller.RestartRunnerAsync), installer.Calls);
        Assert.Contains("status=interrupted runnerId=runner-1 interruptedWorkCount=2", f.Stdout.ToString());
        Assert.DoesNotContain("activeWorks", string.Join('\n', handler.Requests.Select(request => request.RequestUri!.PathAndQuery)));
    }

    [Fact]
    public async Task UpdateRunner_WhenRestartFails_ReleasesConfirmedInterrupt()
    {
        var f = new UpdateTestFactory();
        var installer = new FakeServiceInstaller { RunnerInstalled = true, RestartRunnerResult = 17 };
        var hash = "abcdef1234567890abcdef1234567890abcdef12";
        f.Commands.SetResultFor("git", args => args.SequenceEqual(["rev-parse", "HEAD"]), 0, hash + "\n", "");
        string? updateInterruptId = null;
        var handler = new RecordingHttpHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/api/runner/identity")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        UpdateTestFactory.BuildRunnerIdentityResponse("runner-1", "test-host", hash, "online"),
                        System.Text.Encoding.UTF8,
                        "application/json"),
                });
            }

            if (request.Method == HttpMethod.Post && path == "/api/runner/runner-1/update-interrupt")
            {
                updateInterruptId = ReadUpdateInterruptId(request);
                return Task.FromResult(InterruptResponse(request, "runner-1", []));
            }

            if (request.Method == HttpMethod.Post
                && updateInterruptId is not null
                && path == $"/api/runner/runner-1/update-interrupt/{updateInterruptId}/cancel")
            {
                return Task.FromResult(CancelResponse("runner-1", updateInterruptId));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });
        var updater = f.BuildUpdater(
            handler,
            unitDir: UpdateTestFactory.UnitDir,
            getLocalHostname: () => "test-host",
            serviceInstaller: installer);

        var exitCode = await updater.UpdateRunnerAsync("/repo", dryRun: false);

        Assert.Equal(17, exitCode);
        Assert.NotNull(updateInterruptId);
        Assert.Equal(
            [
                (HttpMethod.Get, "/api/runner/identity"),
                (HttpMethod.Post, "/api/runner/runner-1/update-interrupt"),
                (HttpMethod.Post, $"/api/runner/runner-1/update-interrupt/{updateInterruptId}/cancel"),
            ],
            handler.Requests.Select(request => (request.Method, request.RequestUri!.AbsolutePath)));
        Assert.Contains("Runner update interrupt rollback: status=cancelled runnerId=runner-1.", f.Stdout.ToString());
    }

    [Fact]
    public async Task UpdateRunner_WhenAffectedWorkRemainsUnresolved_ExitsNonSuccessfullyAfterBoundedWait()
    {
        var f = new UpdateTestFactory();
        var installer = new FakeServiceInstaller { RunnerInstalled = true };
        var hash = "abcdef1234567890abcdef1234567890abcdef12";
        f.Commands.SetResultFor("git", args => args.SequenceEqual(["rev-parse", "HEAD"]), 0, hash + "\n", "");
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var handler = new RecordingHttpHandler((request, _) =>
        {
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            if (request.Method == HttpMethod.Get && path == "/api/runner/identity")
            {
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new
                    {
                        runnerId = "runner-1",
                        hostname = "test-host",
                        buildGitHash = hash,
                        status = "online",
                        connectionState = "connected",
                    },
                }));
            }

            if (request.Method == HttpMethod.Post && path == "/api/runner/runner-1/update-interrupt")
            {
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new
                    {
                        runnerId = "runner-1",
                        status = "interrupted",
                        interruptedWorkIds = new[] { "job-1" },
                        interruptedWorkCount = 1,
                        operationId = "runner-update:unresolved",
                        affectedWorks = new[]
                        {
                            new { ownerKind = "agent-job", ownerId = "job-1", workId = "job-1", taskRunId = (string?)null, workType = "agent-job" },
                        },
                    },
                }));
            }

            if (request.Method == HttpMethod.Get
                && path == "/api/runner/runner-1/update-operation/runner-update:unresolved/recovery-status")
            {
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new
                    {
                        operationId = "runner-update:unresolved",
                        runnerId = "runner-1",
                        operationStatus = "pending",
                        complete = false,
                        affectedWorks = new[]
                        {
                            new { ownerKind = "agent-job", ownerId = "job-1", workId = "job-1", taskRunId = (string?)null, workType = "agent-job", status = "unresolved", acknowledged = false },
                        },
                    },
                }));
            }

            return Task.FromResult(RecordingHttpHandler.JsonError("unexpected request", statusCode: HttpStatusCode.NotFound));
        });
        var updater = f.BuildUpdater(
            handler,
            unitDir: UpdateTestFactory.UnitDir,
            getLocalHostname: () => "test-host",
            timeProvider: time,
            runnerRecoveryTimeout: TimeSpan.FromSeconds(1),
            runnerRecoveryPollInterval: TimeSpan.FromMilliseconds(100),
            serviceInstaller: installer);

        var update = updater.UpdateRunnerAsync("/repo", dryRun: false);
        await handler.WaitForRequestCountAsync(4);
        time.Advance(TimeSpan.FromSeconds(1));
        var exitCode = await update;

        Assert.Equal(1, exitCode);
        Assert.Contains("workId=job-1", f.Stderr.ToString());
        Assert.Contains("status=unresolved", f.Stderr.ToString());
        Assert.DoesNotContain("Runner updated successfully.", f.Stdout.ToString());
        Assert.Contains(nameof(FakeServiceInstaller.RestartRunnerAsync), installer.Calls);
    }

    [Fact]
    public async Task UpdateRunner_WhenInterruptIsNotConfirmed_FailsWithoutRestart()
    {
        var f = new UpdateTestFactory();
        var installer = new FakeServiceInstaller { RunnerInstalled = true };
        var hash = "abcdef1234567890abcdef1234567890abcdef12";
        f.Commands.SetResultFor("git", args => args.SequenceEqual(["rev-parse", "HEAD"]), 0, hash + "\n", "");
        var handler = new RecordingHttpHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/api/runner/identity")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        UpdateTestFactory.BuildRunnerIdentityResponse("runner-1", "test-host", hash, "online"),
                        System.Text.Encoding.UTF8,
                        "application/json"),
                });
            }

            if (request.Method == HttpMethod.Post && path == "/api/runner/runner-1/update-interrupt")
            {
                return Task.FromResult(RecordingHttpHandler.JsonError(
                    "runner interrupt unavailable", statusCode: HttpStatusCode.ServiceUnavailable));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });
        var updater = f.BuildUpdater(
            handler,
            unitDir: UpdateTestFactory.UnitDir,
            getLocalHostname: () => "test-host",
            serviceInstaller: installer);

        var exitCode = await updater.UpdateRunnerAsync("/repo", dryRun: false);

        Assert.Equal(1, exitCode);
        Assert.DoesNotContain(nameof(FakeServiceInstaller.RestartRunnerAsync), installer.Calls);
        Assert.Contains("status=unconfirmed", f.Stderr.ToString());
        Assert.Contains("runner service was not restarted", f.Stderr.ToString());
    }

    [Fact]
    public async Task UpdateRunner_BuildsCurrentSourceAndRestarts()
    {
        var f = new UpdateTestFactory();
        f.SeedRunnerUnit();
        var hash = "abcdef1234567890abcdef1234567890abcdef12";
        f.Commands.SetResultFor("git", args => args.SequenceEqual(["rev-parse", "HEAD"]), 0, hash + "\n", "");
        var updater = f.BuildUpdater(
            new SequenceHttpHandler(new ResponseSpec(HttpStatusCode.OK, UpdateTestFactory.BuildRunnerIdentityResponse("runner-1", Environment.MachineName, hash, "online"), "application/json")),
            unitDir: UpdateTestFactory.UnitDir);

        var exitCode = await updater.UpdateRunnerAsync("/repo", dryRun: false);

        Assert.Equal(0, exitCode);
        var npm = Assert.Single(f.Commands.ExecutedCommands, c => c.FileName == "npm");
        Assert.Equal(new[] { "run", "build", "-w", "packages/runner" }, npm.Args);
        Assert.Contains(f.Commands.ExecutedCommands, c =>
            c.FileName == "systemctl" && c.Args.SequenceEqual(new[] { "--user", "restart", "mohist-runner.service" }));
    }

    [Fact]
    public async Task UpdateRunner_WhenIdentityIsUnknown_FailsClosed()
    {
        var f = new UpdateTestFactory();
        f.SeedRunnerUnit();
        var hash = "abcdef1234567890abcdef1234567890abcdef12";
        f.Commands.SetResultFor("git", args => args.SequenceEqual(["rev-parse", "HEAD"]), 0, hash + "\n", "");
        var updater = f.BuildUpdater(
            new SequenceHttpHandler(new ResponseSpec(HttpStatusCode.OK, UpdateTestFactory.BuildRunnerIdentityResponse("runner-1", Environment.MachineName, null, "online"), "application/json")),
            unitDir: UpdateTestFactory.UnitDir);

        var exitCode = await updater.UpdateRunnerAsync("/repo", dryRun: false);

        Assert.Equal(1, exitCode);
        Assert.Contains("unknown-identity", f.Stdout.ToString());
    }

    [Fact]
    public async Task UpdateRunner_WhenRunnerNotInstalled_SkipsWithReason()
    {
        var f = new UpdateTestFactory();
        var updater = f.BuildUpdater(withEnvironment: false, unitDir: UpdateTestFactory.UnitDir);

        var exitCode = await updater.UpdateRunnerAsync("/repo", dryRun: false);

        Assert.Equal(0, exitCode);
        Assert.Empty(f.Commands.ExecutedCommands);
        Assert.Contains("Runner refresh skipped: runner service is not installed", f.Stdout.ToString());
        Assert.Contains("runner-refresh-skipped(runner service is not installed)", f.Stdout.ToString());
    }

    [Fact]
    public async Task UpdateRunner_WhenIdentityMatchesRepoHead_ReportsCurrent()
    {
        var f = new UpdateTestFactory();
        f.SeedRunnerUnit();
        var hash = "abcdef1234567890abcdef1234567890abcdef12";
        f.Commands.SetResultFor("git", args => args.SequenceEqual(new[] { "rev-parse", "HEAD" }), 0, hash + "\n", "");
        var identityResponse = UpdateTestFactory.BuildRunnerIdentityResponse("runner-1", "test-host", hash, "online");
        var updater = f.BuildUpdater(
            new SequenceHttpHandler(new ResponseSpec(HttpStatusCode.OK, identityResponse, "application/json")),
            unitDir: UpdateTestFactory.UnitDir,
            runnerIdentityTimeout: TimeSpan.FromMilliseconds(2000),
            getLocalHostname: () => "test-host");

        var exitCode = await updater.UpdateRunnerAsync("/repo", dryRun: false);

        Assert.Equal(0, exitCode);
        var actual = f.Stdout.ToString();
        Assert.Contains("Runner runtime verification: current", actual);
    }

    [Fact]
    public async Task UpdateRunner_WhenIdentityDiffersFromRepoHead_ReportsStaleRuntime()
    {
        var f = new UpdateTestFactory();
        f.SeedRunnerUnit();
        var repoHead = "0123456789abcdef0123456789abcdef01234567";
        var staleHash = "fedcba9876543210fedcba9876543210fedcba98";
        f.Commands.SetResultFor("git", args => args.SequenceEqual(new[] { "rev-parse", "HEAD" }), 0, repoHead + "\n", "");
        var identityResponse = UpdateTestFactory.BuildRunnerIdentityResponse("runner-1", "test-host", staleHash, "online");
        var updater = f.BuildUpdater(
            new SequenceHttpHandler(new ResponseSpec(HttpStatusCode.OK, identityResponse, "application/json")),
            unitDir: UpdateTestFactory.UnitDir,
            runnerIdentityTimeout: TimeSpan.FromMilliseconds(2000),
            getLocalHostname: () => "test-host");

        var exitCode = await updater.UpdateRunnerAsync("/repo", dryRun: false);

        Assert.Equal(1, exitCode);
        var output = f.Stderr.ToString();
        Assert.Contains("stale-runner-runtime", output);
        Assert.Contains(staleHash, output);
        Assert.Contains(repoHead, output);
    }

    [Fact]
    public async Task UpdateRunner_WhenRunnerDoesNotReconnect_ReportsNotReconnectedEvenWhenBuildInfoMatches()
    {
        var f = new UpdateTestFactory();
        f.SeedRunnerUnit();
        var hash = "9999888877776666555544443333222211110000";
        f.Files.AddDirectory("/repo/packages/runner/dist");
        f.Files.AddFile("/repo/packages/runner/dist/build-info.json", $"{{\"gitHash\":\"{hash}\",\"builtAt\":1700000000}}");
        f.Commands.SetResultFor("git", args => args.SequenceEqual(new[] { "rev-parse", "HEAD" }), 0, hash + "\n", "");
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var handler = CreateIdentityThenNoReconnectHandler(hash);
        var updater = f.BuildUpdater(
            handler,
            unitDir: UpdateTestFactory.UnitDir,
            runnerIdentityTimeout: TimeSpan.FromSeconds(1),
            getLocalHostname: () => "test-host",
            timeProvider: time);

        var update = updater.UpdateRunnerAsync("/repo", dryRun: false);
        await handler.WaitForRequestCountAsync(1);
        time.Advance(TimeSpan.FromSeconds(1));
        var exitCode = await update;

        Assert.Equal(1, exitCode);
        Assert.Contains("runner-not-reconnected", f.Stderr.ToString());
        Assert.Contains("status=unconfirmed", f.Stderr.ToString());
    }

    [Fact]
    public async Task UpdateRunner_WhenRunnerDoesNotReconnectAndBuildInfoStale_ReportsStaleRuntime()
    {
        var f = new UpdateTestFactory();
        f.SeedRunnerUnit();
        var repoHead = "1111222233334444555566667777888899990000";
        var staleHash = "aaaa1111bbbb2222cccc3333dddd4444eeee5555";
        f.Files.AddDirectory("/repo/packages/runner/dist");
        f.Files.AddFile("/repo/packages/runner/dist/build-info.json", $"{{\"gitHash\":\"{staleHash}\",\"builtAt\":1700000000}}");
        f.Commands.SetResultFor("git", args => args.SequenceEqual(new[] { "rev-parse", "HEAD" }), 0, repoHead + "\n", "");
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var handler = CreateIdentityThenNoReconnectHandler(staleHash);
        var updater = f.BuildUpdater(
            handler,
            unitDir: UpdateTestFactory.UnitDir,
            runnerIdentityTimeout: TimeSpan.FromSeconds(1),
            getLocalHostname: () => "test-host",
            timeProvider: time);

        var update = updater.UpdateRunnerAsync("/repo", dryRun: false);
        await handler.WaitForRequestCountAsync(1);
        time.Advance(TimeSpan.FromSeconds(1));
        var exitCode = await update;

        var actual = f.Stderr.ToString();
        Assert.Equal(1, exitCode);
        Assert.Contains("runner-not-reconnected", actual);
        Assert.Contains("status=unconfirmed", actual);
    }
}
