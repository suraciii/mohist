using System.Text.Encodings.Web;
using System.Text.Json;

namespace Mohist.Cli;

internal enum UpdateStage
{
    Start,
    UpdateCli,
    PrepareRunner,
    UpdateServer,
    WaitingForReady,
    RestoreRunner,
    VerifyRuntime,
    Complete,
}

internal enum RuntimeCheckOutcome
{
    Pass,
    Warn,
    Fail,
}

internal sealed record RuntimeCheckResult(string Component, RuntimeCheckOutcome Outcome, string Message);

internal sealed record RuntimeIdentityVerification(
    string ExpectedHash,
    string? ActualHash,
    bool Matches,
    string Reason);

internal sealed record UpdateStageLogEntry(string Stage, string Message, DateTimeOffset At);

internal enum UpdateOutcome
{
    Ready,
    Recovered,
    Failed,
}

internal sealed record CliOutcomeLogEntry(DateTimeOffset At, string Stage, string Message);

internal sealed record CliOutcomeRequest(
    string? JobId,
    string? Status,
    string? Stage,
    string? Outcome,
    string? UnavailableCapability,
    IReadOnlyList<CliOutcomeLogEntry>? Logs,
    string? SourceHead);

internal static class CliOutcomeJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

/// <summary>
/// Per-invocation state bag for the update pipeline. Passed explicitly through each stage
/// rather than held on the facade so collaborators (<see cref="RuntimeConsistencyValidator"/>,
/// <see cref="ServiceReadinessProbe"/>, <see cref="RunnerRefreshVerifier"/>) can stay
/// stateless and trivially testable.
/// </summary>
internal sealed class UpdateContext
{
    public UpdateContext(bool dryRun, string? repoRoot, string? cliPath, CancellationToken cancellationToken)
    {
        DryRun = dryRun;
        RepoRoot = repoRoot;
        CliPath = cliPath;
        CancellationToken = cancellationToken;
        JobId = Guid.NewGuid().ToString("N");
    }

    public bool DryRun { get; }
    public string? RepoRoot { get; }
    public string? CliPath { get; }
    public CancellationToken CancellationToken { get; }

    public string JobId { get; }
    public UpdateStage Stage { get; set; } = UpdateStage.Start;
    public bool RunnerWasRunning { get; set; }
    public bool RunnerInstalled { get; set; }
    public bool RunnerStopped { get; set; }
    public bool RunnerRestored { get; set; }
    public bool Interrupted { get; set; }
    public List<string> Warnings { get; } = new();
    public List<UpdateStageLogEntry> StageLogEntries { get; } = new();
    public List<RuntimeCheckResult> RuntimeChecks { get; } = new();
    public UpdateOutcome? Outcome { get; set; }
    public string? UnavailableCapability { get; set; }
    public string? SourceHead { get; set; }
    public UpdateSource? UpdateSource { get; set; }
    public PreparedRuntimeUpdate? ServerRuntime { get; set; }
    public PreparedRuntimeUpdate? RunnerRuntime { get; set; }
    public bool ServerRuntimeVerified { get; set; }
    public int LastExitCode { get; set; }

    public void RecordWarning(string warning)
    {
        Warnings.Add(warning);
    }

    public void RecordStage(string label, string message)
    {
        StageLogEntries.Add(new UpdateStageLogEntry(label, message, DateTimeOffset.UtcNow));
    }

    public void RecordRuntimeCheck(RuntimeCheckResult check)
    {
        RuntimeChecks.Add(check);
        if (check.Outcome == RuntimeCheckOutcome.Warn)
        {
            Warnings.Add($"{check.Component}: {check.Message}");
        }
    }
}
