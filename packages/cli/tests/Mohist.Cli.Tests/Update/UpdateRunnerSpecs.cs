using System.Net;
using Microsoft.Extensions.Time.Testing;
using Mohist.Cli;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests.Update;

public class UpdateRunnerSpecs
{
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
        var handler = new RecordingHttpHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
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
        var pendingResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new RecordingHttpHandler(async (_, ct) =>
        {
            await pendingResponse.Task.WaitAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
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
    }
}
