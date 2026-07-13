using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.SystemInfo;
using Mohist.Server.UnitTests.Support;
using Xunit;
using EnvironmentAbstractions.TestHelpers;
using static Mohist.Server.UnitTests.SystemSpecs.SystemUpdateServiceTestSupport;

namespace Mohist.Server.UnitTests.SystemSpecs;

public class SystemUpdateServiceInvariantTests
{
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
