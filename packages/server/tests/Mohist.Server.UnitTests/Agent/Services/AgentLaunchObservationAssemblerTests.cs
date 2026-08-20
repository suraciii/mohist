using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Xunit;

namespace Mohist.Server.UnitTests.Agent.Services;

public sealed class AgentLaunchObservationAssemblerTests
{
    private static readonly DateTimeOffset RecoveryDeadline =
        new(2026, 8, 21, 12, 5, 0, TimeSpan.Zero);

    [Fact]
    public void Project_queued_launch_keeps_durable_ids_and_pending_status()
    {
        var observation = AgentLaunchObservationAssembler.Project(
            "project-1",
            "job-1",
            AgentJobStatus.Pending,
            Snapshot(AgentJobStatus.Pending),
            terminal: null,
            Launch(AgentSessionInputAcceptance.Accepted, AgentTurnStatus.Queued),
            sessionInfo: null);

        Assert.NotNull(observation);
        Assert.Equal("pending", observation!.JobStatus);
        Assert.Equal("accepted", observation.InputAcceptance);
        Assert.Equal("queued", observation.TurnStatus);
        Assert.Equal("session-1", observation.SessionId);
        Assert.Equal("input-1", observation.InputId);
        Assert.Equal("turn-1", observation.TurnId);
        Assert.Equal(
            "/api/projects/project-1/agent-jobs/job-1/launch-observation",
            observation.ObservationUrl);
    }

    [Fact]
    public void Project_terminal_launch_includes_job_and_turn_results()
    {
        var observation = AgentLaunchObservationAssembler.Project(
            "project-1",
            "job-1",
            AgentJobStatus.Completed,
            Snapshot(AgentJobStatus.Completed),
            new AgentJobTerminalResult(
                AgentJobStatus.Completed,
                "done",
                "{\"ok\":true}",
                ["artifact-1"],
                null,
                0),
            Launch(
                AgentSessionInputAcceptance.Accepted,
                AgentTurnStatus.Completed,
                new AgentTurnResult("done", "{\"ok\":true}", null, null, 0)),
            sessionInfo: null);

        Assert.NotNull(observation);
        Assert.Equal("completed", observation!.JobStatus);
        Assert.Equal("done", observation.JobMessage);
        Assert.Equal("{\"ok\":true}", observation.JobOutput);
        Assert.Equal(["artifact-1"], observation.JobArtifactUploadIds);
        Assert.Equal(0, observation.JobExitCode);
        Assert.Equal("completed", observation.TurnStatus);
        Assert.Equal("done", observation.TurnResult!.Message);
    }

    [Fact]
    public void Project_unknown_recovery_exposes_reason_and_deadline_without_terminal_fields()
    {
        var snapshot = Snapshot(
            AgentJobStatus.Unknown,
            failureReason: AgentJobFailureReasons.RunnerLost,
            recoveryDeadlineAt: RecoveryDeadline,
            isRecovering: true);

        var observation = AgentLaunchObservationAssembler.Project(
            "project-1",
            "job-1",
            AgentJobStatus.Unknown,
            snapshot,
            terminal: null,
            Launch(AgentSessionInputAcceptance.Accepted, AgentTurnStatus.Unknown),
            sessionInfo: null);

        Assert.NotNull(observation);
        Assert.Equal("recovering", observation!.JobStatus);
        Assert.Equal(AgentJobFailureReasons.RunnerLost, observation.JobFailureReason);
        Assert.Equal(RecoveryDeadline, observation.RecoveryDeadlineAt);
        Assert.Null(observation.JobMessage);
        Assert.Null(observation.JobOutput);
    }

    [Fact]
    public void Project_rejects_a_job_from_another_project()
    {
        var observation = AgentLaunchObservationAssembler.Project(
            "project-1",
            "job-1",
            AgentJobStatus.Completed,
            Snapshot(AgentJobStatus.Completed, projectId: "project-other"),
            terminal: null,
            initialLaunch: null,
            sessionInfo: null);

        Assert.Null(observation);
    }

    [Fact]
    public void Project_rejects_a_job_without_a_session_link()
    {
        var observation = AgentLaunchObservationAssembler.Project(
            "project-1",
            "job-1",
            AgentJobStatus.Pending,
            Snapshot(AgentJobStatus.Pending, agentSessionId: null),
            terminal: null,
            initialLaunch: null,
            sessionInfo: null);

        Assert.Null(observation);
    }

    [Theory]
    [InlineData(AgentJobStatus.Unknown, true, "recovering")]
    [InlineData(AgentJobStatus.Unknown, false, "unknown")]
    [InlineData(AgentJobStatus.Running, true, "running")]
    [InlineData(AgentJobStatus.Failed, true, "failed")]
    public void ToJobStatusString_ProjectsRecoveringWithoutChangingPersistedUnknown(
        AgentJobStatus status,
        bool isRecovering,
        string expected)
    {
        Assert.Equal(expected,
            AgentLaunchObservationAssembler.ToJobStatusString(status, isRecovering));
    }

    private static AgentJobRuntimeSnapshot Snapshot(
        AgentJobStatus status,
        string projectId = "project-1",
        string? agentSessionId = "session-1",
        string? failureReason = null,
        DateTimeOffset? recoveryDeadlineAt = null,
        bool isRecovering = false) =>
        new(
            status,
            RunnerId: null,
            CurrentWorkId: null,
            FailureReason: failureReason,
            ProjectId: projectId,
            AgentSessionId: agentSessionId,
            InitialInputId: "input-1",
            InitialTurnId: "turn-1",
            RecoveryDeadlineAt: recoveryDeadlineAt,
            IsRecovering: isRecovering);

    private static AgentInitialLaunchSnapshot Launch(
        AgentSessionInputAcceptance acceptance,
        AgentTurnStatus turnStatus,
        AgentTurnResult? result = null) =>
        new(
            "session-1",
            new AgentSessionInputRecord(
                "input-1",
                0,
                "prompt",
                "manual",
                acceptance,
                new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc)),
            new AgentTurnRecord(
                "turn-1",
                0,
                ["input-1"],
                turnStatus,
                Result: result));
}
