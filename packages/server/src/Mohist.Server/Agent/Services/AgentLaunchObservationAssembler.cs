using Mohist.Server.Agent.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;

namespace Mohist.Server.Agent.Services;

/// <summary>
/// Composite, read-only observation for a single launch. Returns the
/// canonical Job+Session+Input+Turn
/// facts and the composite observation URL; Job result fields come
/// from <see cref="IAgentJobGrain"/> and Session/Input/Turn/activity/
/// transcript fields come from <see cref="IAgentSessionGrain"/>. The
/// assembler only composes read models — it does not own any state.
/// <para>
/// Web and CLI use the same DTO vocabulary for accepted, queued,
/// executing, completed, failed, and Unknown. Web navigates to the
/// Session via the returned SessionId and re-reads the observation
/// surface after reconnect. CLI prints the four IDs and the
/// observation URL.
/// </para>
/// </summary>
public sealed record AgentLaunchObservationDto(
    string JobId,
    string JobStatus,
    string? JobMessage,
    string? JobOutput,
    IReadOnlyList<string>? JobArtifactUploadIds,
    string? JobFailureReason,
    int? JobExitCode,
    string SessionId,
    string SessionActivity,
    string? SessionRuntime,
    string TranscriptUrl,
    string? InputId,
    string InputAcceptance,
    string? TurnId,
    string TurnStatus,
    AgentTurnResultDto? TurnResult,
    string ObservationUrl,
    DateTimeOffset? RecoveryDeadlineAt = null);

public sealed record AgentTurnResultDto(
    string? Message,
    string? Output,
    string? FailureReason,
    string? FailureCategory,
    int? ExitCode);

/// <summary>
/// Read-only assembler for <see cref="AgentLaunchObservationDto"/>
/// Composes the Job and Session owners'
/// authoritative state into the composite DTO; never mints new
/// SessionInput, AgentTurn, or AgentJob resources and never advances
/// the Job lifecycle. Returns <c>null</c> when the Job does not
/// resolve, the Session link is missing, or the Job does not belong
/// to the supplied Project (project isolation).
/// </summary>
public sealed class AgentLaunchObservationAssembler
{
    private readonly IGrainFactory _grains;

    public AgentLaunchObservationAssembler(IGrainFactory grains)
    {
        _grains = grains;
    }

    public async Task<AgentLaunchObservationDto?> ReadAsync(
        string projectId,
        string jobId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        var jobGrain = _grains.GetGrain<IAgentJobGrain>(jobId);
        var snapshot = await jobGrain.GetRuntimeSnapshotAsync();
        if (!string.Equals(snapshot.ProjectId, projectId, StringComparison.Ordinal))
        {
            return null;
        }

        var status = await jobGrain.GetStatusAsync();
        string? jobMessage = null;
        string? jobOutput = null;
        IReadOnlyList<string>? jobArtifactUploadIds = null;
        string? jobFailureReason = null;
        int? jobExitCode = null;
        if (status is AgentJobStatus.Completed or AgentJobStatus.Failed or AgentJobStatus.Cancelled)
        {
            var terminal = await jobGrain.GetTerminalResultAsync();
            jobMessage = terminal.Message;
            jobOutput = terminal.Output;
            jobArtifactUploadIds = terminal.ArtifactUploadIds;
            jobFailureReason = terminal.FailureReason;
            jobExitCode = terminal.ExitCode;
        }
        else if (status == AgentJobStatus.Unknown)
        {
            // Unknown is nonterminal and non-dispatchable. A future
            // runner-loss deadline projects it as recovering while the
            // recorded reason remains visible to the caller.
            jobFailureReason = snapshot.FailureReason;
        }

        var sessionId = snapshot.AgentSessionId;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        var sessionGrain = _grains.GetGrain<IAgentSessionGrain>(sessionId);
        var initialLaunch = await sessionGrain.GetInitialLaunchAsync();
        var sessionInfo = await sessionGrain.GetAsync();

        var transcriptUrl =
            $"/api/projects/{Uri.EscapeDataString(projectId)}/agent-sessions/{Uri.EscapeDataString(sessionId)}/transcript";
        var observationUrl =
            $"/api/projects/{Uri.EscapeDataString(projectId)}/agent-jobs/{Uri.EscapeDataString(jobId)}/launch-observation";
        var inputAcceptance = initialLaunch?.Input is null
            ? "absent"
            : initialLaunch.Input.Acceptance switch
            {
                AgentSessionInputAcceptance.Accepted => "accepted",
                AgentSessionInputAcceptance.Pending => "pending",
                AgentSessionInputAcceptance.Rejected => "rejected",
                _ => "absent",
            };
        var turnStatus = initialLaunch?.Turn is null
            ? "absent"
            : ToTurnStatusString(initialLaunch.Turn.Status);
        var turnResult = initialLaunch?.Turn?.Result is null
            ? null
            : new AgentTurnResultDto(
                Message: initialLaunch.Turn.Result.Message,
                Output: initialLaunch.Turn.Result.Output,
                FailureReason: initialLaunch.Turn.Result.FailureReason,
                FailureCategory: initialLaunch.Turn.Result.FailureCategory,
                ExitCode: initialLaunch.Turn.Result.ExitCode);

        return new AgentLaunchObservationDto(
            JobId: jobId,
            JobStatus: ToJobStatusString(status, snapshot.IsRecovering),
            JobMessage: jobMessage,
            JobOutput: jobOutput,
            JobArtifactUploadIds: jobArtifactUploadIds,
            JobFailureReason: jobFailureReason,
            JobExitCode: jobExitCode,
            SessionId: sessionId,
            SessionActivity: sessionInfo?.Status ?? "absent",
            SessionRuntime: sessionInfo?.Runtime,
            TranscriptUrl: transcriptUrl,
            InputId: initialLaunch?.Input?.Id ?? snapshot.InitialInputId,
            InputAcceptance: inputAcceptance,
            TurnId: initialLaunch?.Turn?.Id ?? snapshot.InitialTurnId,
            TurnStatus: turnStatus,
            TurnResult: turnResult,
            ObservationUrl: observationUrl,
            RecoveryDeadlineAt: snapshot.IsRecovering ? snapshot.RecoveryDeadlineAt : null);
    }

    internal static string ToJobStatusString(AgentJobStatus status, bool isRecovering = false) =>
        isRecovering && status == AgentJobStatus.Unknown
            ? "recovering"
            : status switch
    {
        AgentJobStatus.Pending => "pending",
        AgentJobStatus.Running => "running",
        AgentJobStatus.Completed => "completed",
        AgentJobStatus.Failed => "failed",
        AgentJobStatus.Cancelled => "cancelled",
        AgentJobStatus.Unknown => "unknown",
        AgentJobStatus.RecoverablyInterrupted => "recoverably-interrupted",
        _ => "unknown",
    };

    private static string ToTurnStatusString(AgentTurnStatus status) => status switch
    {
        AgentTurnStatus.Queued => "queued",
        AgentTurnStatus.Executing => "executing",
        AgentTurnStatus.Completed => "completed",
        AgentTurnStatus.Failed => "failed",
        AgentTurnStatus.Unknown => "unknown",
        AgentTurnStatus.Cancelled => "cancelled",
        _ => "unknown",
    };
}
