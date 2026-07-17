using Microsoft.Extensions.Time.Testing;
using Mohist.Server.SystemInfo;
using Xunit;
using static Mohist.Server.SpecTests.Specs.SystemSpecs.SystemUpdateTestFactory;

namespace Mohist.Server.SpecTests.Specs.SystemSpecs;

public class SystemUpdateCliOutcomeSpecs
{
    [Fact]
    public async Task RecordCliOutcomeAsync_PersistsOutcomeViaStore()
    {
        var store = new InMemoryUpdateStore();
        var service = CreateService(
            new SequencedSystemInfo(CreateInfo(runningGitHash: "newhash", sourceHead: "newhash")),
            store,
            new RecordingCommandRunner(),
            new StubReadinessProbe(new(true, true, true, "/assets/app.js", null)));

        var response = await service.RecordCliOutcomeAsync(new SystemUpdateOutcomeRequest(
            JobId: "cli-job-1",
            Status: "succeeded",
            Stage: "Verifying workflow runtime",
            Outcome: "succeeded",
            UnavailableCapability: null,
            Logs: [new SystemUpdateLogEntry(FixedNow, "Verifying workflow runtime", "all checks passed")],
            SourceHead: "newhash",
            SourcePath: "/repo",
            ServerUnit: "mohist.service",
            RunnerUnit: "mohist-runner.service"));

        Assert.Equal("succeeded", response.Status);
        Assert.Equal("succeeded", response.Outcome);
        Assert.Null(response.UnavailableCapability);
        Assert.Equal("cli-job-1", response.JobId);
        Assert.Equal("newhash", response.SourceHead);

        var latest = await store.GetLatestAsync();
        Assert.NotNull(latest);
        Assert.Equal("cli-job-1", latest!.JobId);
        Assert.Equal("succeeded", latest.Status);
        Assert.Equal("succeeded", latest.Outcome);
    }

    [Fact]
    public async Task RecordCliOutcomeAsync_AppendsRequestLogsToPersistedJobLog()
    {
        var store = new InMemoryUpdateStore();
        var service = CreateService(
            new SequencedSystemInfo(CreateInfo(runningGitHash: "newhash", sourceHead: "newhash")),
            store,
            new RecordingCommandRunner(),
            new StubReadinessProbe(new(true, true, true, "/assets/app.js", null)));

        var stageTime = FixedNow;
        var request = new SystemUpdateOutcomeRequest(
            JobId: "cli-job-logs",
            Status: "succeeded",
            Stage: "Verifying workflow runtime",
            Outcome: "succeeded",
            UnavailableCapability: null,
            Logs:
            [
                new SystemUpdateLogEntry(stageTime, "Updating CLI", "starting"),
                new SystemUpdateLogEntry(stageTime, "Preparing workflow runner", "runner stopped"),
                new SystemUpdateLogEntry(stageTime, "Verifying workflow runtime", "all checks passed"),
            ],
            SourceHead: "newhash",
            SourcePath: "/repo",
            ServerUnit: "mohist.service",
            RunnerUnit: "mohist-runner.service");

        await service.RecordCliOutcomeAsync(request);

        var latest = await store.GetLatestAsync();
        Assert.NotNull(latest);
        Assert.Contains(latest!.Logs, entry => entry.Stage == "Updating CLI" && entry.Message == "starting");
        Assert.Contains(latest.Logs, entry => entry.Stage == "Preparing workflow runner" && entry.Message == "runner stopped");
        Assert.Contains(latest.Logs, entry => entry.Stage == "Verifying workflow runtime" && entry.Message == "all checks passed");
        Assert.Contains(latest.Logs, entry => entry.Message.StartsWith("CLI reported outcome"));
    }

    [Fact]
    public async Task RecordCliOutcomeAsync_MarksStaleWebJobAsSuperseded()
    {
        var store = new InMemoryUpdateStore();
        var now = FixedNow;
        await store.SaveAsync(new SystemUpdateJobState(
            "web-job-1",
            "waiting-for-reconnect",
            "Waiting for reconnect",
            true,
            "oldhash",
            "oldsource",
            "/repo",
            "mohist.service",
            "mohist-runner.service",
            "Waiting for old restart",
            [new SystemUpdateLogEntry(now, "Waiting for reconnect", "Waiting for old restart")],
            now,
            now,
            null));

        var service = CreateService(
            new SequencedSystemInfo(CreateInfo(runningGitHash: "newhash", sourceHead: "newhash")),
            store,
            new RecordingCommandRunner(),
            new StubReadinessProbe(new(true, true, true, "/assets/app.js", null)));

        var response = await service.RecordCliOutcomeAsync(new SystemUpdateOutcomeRequest(
            JobId: "cli-job-1",
            Status: "succeeded",
            Stage: "Verifying workflow runtime",
            Outcome: "succeeded",
            SourceHead: "newhash"));

        Assert.Equal("cli-job-1", response.JobId);
        Assert.Equal("succeeded", response.Status);

        var latest = await store.GetLatestAsync();
        Assert.Equal("cli-job-1", latest!.JobId);
    }

    [Fact]
    public async Task RecordCliOutcomeAsync_AlwaysPersistsWithoutAcquiringLock()
    {
        var store = new InMemoryUpdateStore(acquireLock: true);
        var now = FixedNow;
        await store.SaveAsync(new SystemUpdateJobState(
            "web-job-active",
            "running",
            "Building",
            true,
            "oldhash",
            "newhash",
            "/repo",
            "mohist.service",
            "mohist-runner.service",
            null,
            [new SystemUpdateLogEntry(now, "Building", "Building")],
            now,
            now,
            null));

        var service = CreateService(
            new SequencedSystemInfo(CreateInfo(runningGitHash: "newhash", sourceHead: "newhash")),
            store,
            new RecordingCommandRunner(),
            new StubReadinessProbe(new(true, true, true, "/assets/app.js", null)));

        var response = await service.RecordCliOutcomeAsync(new SystemUpdateOutcomeRequest(
            JobId: "cli-job-1",
            Status: "succeeded",
            Stage: "Ready",
            Outcome: "succeeded",
            SourceHead: "newhash"));

        Assert.Equal("cli-job-1", response.JobId);
        Assert.Equal("succeeded", response.Status);

        var latest = await store.GetLatestAsync();
        Assert.Equal("cli-job-1", latest!.JobId);
    }

    [Fact]
    public async Task RecordCliOutcomeAsync_NewOutcomeReplacesPriorTerminalJob()
    {
        var store = new InMemoryUpdateStore();
        var now = FixedNow;
        await store.SaveAsync(new SystemUpdateJobState(
            "other-job-terminal",
            "succeeded",
            "Ready",
            true,
            "oldhash",
            "newhash",
            "/repo",
            "mohist.service",
            "mohist-runner.service",
            null,
            [new SystemUpdateLogEntry(now, "Ready", "ready")],
            now,
            now,
            now,
            "succeeded",
            null));

        var service = CreateService(
            new SequencedSystemInfo(CreateInfo(runningGitHash: "newhash", sourceHead: "newhash")),
            store,
            new RecordingCommandRunner(),
            new StubReadinessProbe(new(true, true, true, "/assets/app.js", null)));

        var response = await service.RecordCliOutcomeAsync(new SystemUpdateOutcomeRequest(
            JobId: "cli-job-1",
            Status: "succeeded",
            Stage: "Verifying workflow runtime",
            Outcome: "succeeded",
            SourceHead: "newhash"));

        Assert.Equal("cli-job-1", response.JobId);
        Assert.Equal("succeeded", response.Status);

        var latest = await store.GetLatestAsync();
        Assert.Equal("cli-job-1", latest!.JobId);
        Assert.Equal("succeeded", latest.Status);
    }

    [Fact]
    public async Task RecordCliOutcomeAsync_RejectsUnknownStatus()
    {
        var store = new InMemoryUpdateStore();
        var service = CreateService(
            new SequencedSystemInfo(CreateInfo(runningGitHash: "newhash", sourceHead: "newhash")),
            store,
            new RecordingCommandRunner(),
            new StubReadinessProbe(new(true, true, true, "/assets/app.js", null)));

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => service.RecordCliOutcomeAsync(new SystemUpdateOutcomeRequest(
            JobId: "cli-job-bogus",
            Status: "bogus",
            Stage: "Ready",
            Outcome: "succeeded",
            SourceHead: "newhash")));

        Assert.Contains("bogus", ex.Message);
    }

    [Fact]
    public async Task PersistTransitionAsync_ReleasesLockOnlyAfterSave()
    {
        var store = new OrderTrackingStore();
        var service = CreateService(
            new SequencedSystemInfo(CreateInfo(runningGitHash: "newhash", sourceHead: "newhash")),
            store,
            new RecordingCommandRunner(),
            new StubReadinessProbe(new(true, true, true, "/assets/app.js", null)));

        await service.RecordCliOutcomeAsync(new SystemUpdateOutcomeRequest(
            JobId: "cli-job-1",
            Status: "succeeded",
            Stage: "Ready",
            Outcome: "succeeded",
            SourceHead: "newhash"));

        var saveIndex = store.Events.IndexOf("Save");
        var releaseIndex = store.Events.IndexOf("ReleaseLock");
        Assert.True(saveIndex >= 0);
        Assert.True(releaseIndex >= 0);
        Assert.True(saveIndex < releaseIndex, "ReleaseLockAsync must run strictly after SaveAsync");
    }

}
