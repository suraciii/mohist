using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.SystemInfo;
using Mohist.Server.ComponentSpecs.Support;
using Xunit;
using EnvironmentAbstractions.TestHelpers;
using static Mohist.Server.ComponentSpecs.Specs.SystemSpecs.SystemUpdateServiceTestSupport;

namespace Mohist.Server.ComponentSpecs.Specs.SystemSpecs;

public class SystemUpdateServiceInvariantSpecs
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

    [Fact]
    public void SourceAudit_FailedStateIsDefinedOnlyInCreateFailedTransition()
    {
        var source = ReadSource();
        var composerStart = source.IndexOf("private (SystemUpdateJobState State, SystemUpdateLogEntry LogEntry) CreateFailedTransition", StringComparison.Ordinal);
        if (composerStart < 0)
        {
            composerStart = source.IndexOf("private static (SystemUpdateJobState State, SystemUpdateLogEntry LogEntry) CreateFailedTransition", StringComparison.Ordinal);
        }
        Assert.True(composerStart >= 0, "CreateFailedTransition method not found");
        var composerEnd = FindMethodEnd(source, composerStart);

        var matches = Regex.Matches(source, @"state\s+with\s*\{[^}]*Status\s*=\s*""failed""", RegexOptions.Singleline);
        Assert.Single(matches);
        Assert.InRange(matches[0].Index, composerStart, composerEnd);
    }

    [Fact]
    public void SourceAudit_SaveAsyncOnlyInSharedHelpersAndStartAsync()
    {
        var source = ReadSource();
        var persistStart = source.IndexOf("private async Task<SystemUpdateJobState> PersistTransitionAsync", StringComparison.Ordinal);
        var persistEnd = FindMethodEnd(source, persistStart);
        var startAsyncStart = source.IndexOf("public async Task<(bool Started, string? Error, string? Code, SystemUpdateStatusResponse? Status)> StartAsync", StringComparison.Ordinal);
        var startAsyncEnd = FindMethodEnd(source, startAsyncStart);

        var matches = Regex.Matches(source, @"await\s+_store\.SaveAsync\s*\(");
        foreach (Match match in matches)
        {
            var inPersist = match.Index >= persistStart && match.Index <= persistEnd;
            var inStartAsync = match.Index >= startAsyncStart && match.Index <= startAsyncEnd;
            Assert.True(inPersist || inStartAsync,
                $"_store.SaveAsync call at position {match.Index} is not inside PersistTransitionAsync or StartAsync");
        }
    }

    [Fact]
    public void SourceAudit_AppendLogInvocationsStayOnSharedHelperPath()
    {
        var source = ReadSource();
        var applyLogStart = source.IndexOf("private SystemUpdateJobState ApplyTransitionLog", StringComparison.Ordinal);
        if (applyLogStart < 0)
        {
            applyLogStart = source.IndexOf("private static SystemUpdateJobState ApplyTransitionLog", StringComparison.Ordinal);
        }
        Assert.True(applyLogStart >= 0, "ApplyTransitionLog method not found");
        var applyLogEnd = FindMethodEnd(source, applyLogStart);
        var recordOutcomeStart = source.IndexOf("public async Task<SystemUpdateStatusResponse> RecordCliOutcomeAsync", StringComparison.Ordinal);
        Assert.True(recordOutcomeStart >= 0, "RecordCliOutcomeAsync method not found");
        var recordOutcomeEnd = FindMethodEnd(source, recordOutcomeStart);

        var matches = Regex.Matches(source, @"(?<!IReadOnlyList<SystemUpdateLogEntry>\s)AppendLog\s*\(");
        Assert.Equal(2, matches.Count);
        foreach (Match match in matches)
        {
            var inApplyLog = match.Index >= applyLogStart && match.Index <= applyLogEnd;
            var inRecordOutcome = match.Index >= recordOutcomeStart && match.Index <= recordOutcomeEnd;
            Assert.True(inApplyLog || inRecordOutcome,
                $"AppendLog invocation at position {match.Index} is not inside ApplyTransitionLog or CLI outcome log ingestion");
        }
    }

    [Fact]
    public void SourceAudit_SaveIfCurrentAsyncOnlyInPersistTransitionAsync()
    {
        var source = ReadSource();
        var persistStart = source.IndexOf("private async Task<SystemUpdateJobState> PersistTransitionAsync", StringComparison.Ordinal);
        var persistEnd = FindMethodEnd(source, persistStart);

        var matches = Regex.Matches(source, @"await\s+_store\.SaveIfCurrentAsync\s*\(");
        foreach (Match match in matches)
        {
            Assert.True(match.Index >= persistStart && match.Index <= persistEnd,
                $"_store.SaveIfCurrentAsync call at position {match.Index} is not inside PersistTransitionAsync");
        }
    }

    [Fact]
    public void SourceAudit_ReleaseLockAsyncOnlyInSharedHelpersAndRunUpdateFinally()
    {
        var source = ReadSource();
        var persistStart = source.IndexOf("private async Task<SystemUpdateJobState> PersistTransitionAsync", StringComparison.Ordinal);
        var persistEnd = FindMethodEnd(source, persistStart);
        var runUpdateStart = source.IndexOf("private async Task RunUpdateAsync", StringComparison.Ordinal);
        var runUpdateEnd = FindMethodEnd(source, runUpdateStart);

        var matches = Regex.Matches(source, @"await\s+_store\.ReleaseLockAsync\s*\(");
        foreach (Match match in matches)
        {
            var inPersist = match.Index >= persistStart && match.Index <= persistEnd;
            var inRunUpdate = match.Index >= runUpdateStart && match.Index <= runUpdateEnd;
            Assert.True(inPersist || inRunUpdate,
                $"_store.ReleaseLockAsync call at position {match.Index} is not inside PersistTransitionAsync or RunUpdateAsync");
        }
    }

    [Fact]
    public void SourceAudit_LogCapDefinedOnce()
    {
        var source = ReadSource();
        Assert.Contains("private const int MaxLogEntries = 200;", source);
        var capMatches = Regex.Matches(source, @"\b200\b");
        Assert.Single(capMatches);
    }

    [Fact]
    public void SourceAudit_IsUpdateEnabledUsesExplicitControlFlow()
    {
        var source = ReadSource();
        var methodStart = source.IndexOf("private bool IsUpdateEnabled()", StringComparison.Ordinal);
        Assert.True(methodStart >= 0, "IsUpdateEnabled method not found");
        var methodEnd = FindMethodEnd(source, methodStart);
        var body = source.Substring(methodStart, methodEnd - methodStart);

        var singleLinePattern = new Regex(
            @"return\s+string\.IsNullOrWhiteSpace\s*\([^)]*\)\s*\|\|\s*bool\.TryParse\s*\([^)]*\)\s*&&\s*",
            RegexOptions.Singleline);
        Assert.Empty(singleLinePattern.Matches(body));

        Assert.Contains("if (!string.IsNullOrWhiteSpace(", body);
        Assert.Contains("bool.TryParse(", body);
        Assert.Matches(new Regex(@"return\s+true\s*;"), body);
    }

    private static string SourcePath => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..",
        "src", "Mohist.Server", "SystemInfo", "SystemUpdateService.cs"));

    private static string ReadSource() => File.ReadAllText(SourcePath);

    private static int FindMethodEnd(string source, int methodStart)
    {
        var match = Regex.Match(source.Substring(methodStart), @"\n    (?:private|public|internal) ");
        return match.Success ? methodStart + match.Index : source.Length;
    }


}
