using Mohist.Server.Runner.Services.SignalR;
using Xunit;

namespace Mohist.Server.UnitTests.Runner.SignalR;

public class RunnerConnectionTrackerRuntimeIdentityTests
{
    [Fact]
    public async Task WaitForRuntimeIdentityAsync_CompletesOnlyAfterMatchingReportAndConnection()
    {
        var tracker = new RunnerConnectionTracker();
        const string runnerId = "runner-managed";
        const string generation = "generation-42";
        const string sessionToken = "session-42";
        const string hash = "0123456789abcdef0123456789abcdef01234567";
        const string digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        tracker.ReportRuntime(runnerId, generation, hash, digest, sessionToken);
        var waiting = tracker.WaitForRuntimeIdentityAsync(runnerId, generation, CancellationToken.None);

        Assert.False(waiting.IsCompleted);

        tracker.Register(runnerId, "connection-new", generation, hash, digest, sessionToken);
        var identity = await waiting;

        Assert.NotNull(identity);
        Assert.True(identity.IsOnline);
        Assert.Equal("connection-new", identity.ConnectionId);
        Assert.Equal(hash, identity.BuildGitHash);
        Assert.Equal(digest, identity.ArtifactDigest);
    }

    [Fact]
    public void GetRuntimeIdentity_DoesNotTreatOldRuntimeAsOnlineWhenReportedSourceDiffers()
    {
        var tracker = new RunnerConnectionTracker();
        const string digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        const string sessionToken = "session-42";

        tracker.ReportRuntime("runner-managed", "generation-42", "oldhash", digest, sessionToken);
        tracker.Register("runner-managed", "connection-new", "generation-42", "newhash", digest, sessionToken);

        var identity = tracker.GetRuntimeIdentity("runner-managed", "generation-42");

        Assert.NotNull(identity);
        Assert.False(identity.IsOnline);
        Assert.Equal("connection-new", identity.ConnectionId);
        Assert.Equal("newhash", identity.BuildGitHash);
    }

    [Fact]
    public void GetRuntimeIdentity_DoesNotTreatConnectedAsOnlineWhenReportedDigestDiffers()
    {
        var tracker = new RunnerConnectionTracker();
        const string hash = "0123456789abcdef0123456789abcdef01234567";
        const string oldDigest = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";
        const string candidateDigest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        const string sessionToken = "session-42";

        tracker.ReportRuntime("runner-managed", "generation-42", hash, oldDigest, sessionToken);
        tracker.Register("runner-managed", "connection-new", "generation-42", hash, candidateDigest, sessionToken);

        var identity = tracker.GetRuntimeIdentity("runner-managed", "generation-42");

        Assert.NotNull(identity);
        Assert.False(identity.IsOnline);
        Assert.Equal(candidateDigest, identity.ArtifactDigest);
    }

    [Theory]
    [InlineData(null, "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("invalid/source", "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("0123456789abcdef0123456789abcdef01234567", null)]
    [InlineData("0123456789abcdef0123456789abcdef01234567", "not-a-sha256-digest")]
    public void ManagedRuntimeReportAndSignalRRegistration_RejectMissingOrInvalidIdentity(
        string? sourceHash,
        string? artifactDigest)
    {
        var tracker = new RunnerConnectionTracker();
        const string runnerId = "runner-managed";
        const string generation = "2";
        const string sessionToken = "session-2";

        Assert.False(tracker.ReportRuntime(runnerId, generation, sourceHash, artifactDigest, sessionToken));
        Assert.False(tracker.Register(runnerId, "connection-2", generation, sourceHash, artifactDigest, sessionToken));

        Assert.Null(tracker.GetRuntimeIdentity(runnerId, generation));
        Assert.Null(tracker.GetConnectionId(runnerId));
    }

    [Fact]
    public void InvalidManagedIdentity_DoesNotAdvanceTheFenceOrReplaceTheVerifiedInstance()
    {
        var tracker = new RunnerConnectionTracker();
        const string runnerId = "runner-managed";
        const string hash = "0123456789abcdef0123456789abcdef01234567";
        const string digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        Assert.True(tracker.ReportRuntime(runnerId, "1", hash, digest, "session-1"));
        Assert.True(tracker.Register(runnerId, "connection-1", "1", hash, digest, "session-1"));

        Assert.False(tracker.ReportRuntime(runnerId, "2", "invalid/source", digest, "session-2"));
        Assert.False(tracker.Register(runnerId, "connection-2", "2", hash, "not-a-sha256-digest", "session-2"));

        var active = tracker.GetRuntimeIdentity(runnerId, "1");
        Assert.NotNull(active);
        Assert.True(active.IsOnline);
        Assert.Equal("connection-1", active.ConnectionId);
        Assert.Null(tracker.GetRuntimeIdentity(runnerId, "2"));
    }

    [Fact]
    public void GetConnectionId_FencesOldManagedConnectionWhenNewGenerationReports()
    {
        var tracker = new RunnerConnectionTracker();
        const string runnerId = "runner-managed";
        const string hash = "0123456789abcdef0123456789abcdef01234567";
        const string digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        Assert.True(tracker.ReportRuntime(runnerId, "1", hash, digest, "session-1"));
        Assert.True(tracker.Register(runnerId, "connection-1", "1", hash, digest, "session-1"));
        Assert.Equal("connection-1", tracker.GetConnectionId(runnerId));

        Assert.True(tracker.ReportRuntime(runnerId, "2", hash, digest, "session-2"));
        Assert.Null(tracker.GetConnectionId(runnerId));

        Assert.True(tracker.Register(runnerId, "connection-2", "2", hash, digest, "session-2"));
        Assert.Equal("connection-2", tracker.GetConnectionId(runnerId));
    }

    [Fact]
    public void GetRuntimeIdentity_UsesRunnerIdAndGenerationRatherThanHostnameSelection()
    {
        var tracker = new RunnerConnectionTracker();
        const string digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        tracker.ReportRuntime("runner-a", "generation-a", "hasha", digest, "session-a");
        tracker.Register("runner-a", "connection-a", "generation-a", "hasha", digest, "session-a");
        tracker.ReportRuntime("runner-b", "generation-b", "hashb", digest, "session-b");
        tracker.Register("runner-b", "connection-b", "generation-b", "hashb", digest, "session-b");

        var expected = tracker.GetRuntimeIdentity("runner-b", "generation-b");
        var staleGeneration = tracker.GetRuntimeIdentity("runner-b", "generation-a");

        Assert.NotNull(expected);
        Assert.True(expected.IsOnline);
        Assert.Equal("runner-b", expected.RunnerId);
        Assert.Equal("hashb", expected.BuildGitHash);
        Assert.Null(staleGeneration);
    }

    [Fact]
    public void GetRuntimeIdentity_RejectsManagedInstanceWithoutActivationSessionToken()
    {
        var tracker = new RunnerConnectionTracker();
        const string runnerId = "runner-managed";
        const string generation = "7";
        const string hash = "0123456789abcdef0123456789abcdef01234567";
        const string digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        Assert.False(tracker.ReportRuntime(runnerId, generation, hash, digest));
        Assert.False(tracker.Register(runnerId, "connection-7", generation, hash, digest));
        Assert.Null(tracker.GetRuntimeIdentity(runnerId, generation));
    }

    [Fact]
    public void UnregisterAndGetSessions_DelayedOldGenerationCannotRemoveCurrentInstance()
    {
        var tracker = new RunnerConnectionTracker();
        const string runnerId = "runner-managed";
        const string hash = "0123456789abcdef0123456789abcdef01234567";
        const string digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        Assert.True(tracker.ReportRuntime(runnerId, "1", hash, digest, "session-1"));
        Assert.True(tracker.Register(runnerId, "connection-1", "1", hash, digest, "session-1"));
        tracker.RegisterSession(runnerId, "session-work");
        Assert.True(tracker.ReportRuntime(runnerId, "2", hash, digest, "session-2"));
        Assert.True(tracker.Register(runnerId, "connection-2", "2", hash, digest, "session-2"));

        var delayedDisconnect = tracker.UnregisterAndGetSessions(
            runnerId,
            "1",
            "connection-1",
            "session-1");
        var current = tracker.GetRuntimeIdentity(runnerId, "2");

        Assert.Empty(delayedDisconnect);
        Assert.NotNull(current);
        Assert.True(current.IsOnline);
        Assert.Equal("connection-2", current.ConnectionId);
        Assert.Equal(["session-work"], tracker.UnregisterAndGetSessions(runnerId, "2", "connection-2", "session-2"));
    }

    [Fact]
    public async Task Register_DelayedOlderGenerationCannotOverwriteNewerConnection()
    {
        var tracker = new RunnerConnectionTracker();
        const string runnerId = "runner-managed";
        const string hash = "0123456789abcdef0123456789abcdef01234567";
        const string digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var olderRegisterEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOlderRegister = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var delayedOlderRegister = Task.Run(async () =>
        {
            olderRegisterEntered.TrySetResult();
            await releaseOlderRegister.Task;
            return tracker.Register(runnerId, "connection-1", "1", hash, digest, "session-1");
        });

        await olderRegisterEntered.Task;
        Assert.True(tracker.ReportRuntime(runnerId, "2", hash, digest, "session-2"));
        Assert.True(tracker.Register(runnerId, "connection-2", "2", hash, digest, "session-2"));
        releaseOlderRegister.TrySetResult();

        Assert.False(await delayedOlderRegister);
        var current = tracker.GetRuntimeIdentity(runnerId, "2");
        Assert.NotNull(current);
        Assert.True(current.IsOnline);
        Assert.Equal("connection-2", current.ConnectionId);
    }
}
