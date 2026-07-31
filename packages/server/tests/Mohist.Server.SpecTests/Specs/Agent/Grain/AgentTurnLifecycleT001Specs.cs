using System.Text.Json;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Orleans;
using Orleans.Core.Internal;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Grain;

[Collection("AgentJobGrain")]
public class AgentTurnLifecycleT001Specs : AgentJobGrainTestSupport
{
    public AgentTurnLifecycleT001Specs(AgentJobGrainFixture fixture) : base(fixture)
    {
    }

    private async Task<IAgentSessionGrain> OpenIdleSessionAsync(string sessionId, string projectId)
    {
        var grain = Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.OpenAsync(new OpenAgentSessionCommand(
            RunnerId: string.Empty,
            AgentRuntime: "opencode",
            WorkDir: "/tmp/turn-522",
            Metadata: new AgentSessionMetadata(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
                    [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
                    [GenericAgentSessionMetadata.AgentId] = "agent-test",
                })));
        await grain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(
            AgentSessionId: "runtime-1",
            WorkDir: "/tmp/turn-522"));
        return grain;
    }

    private static AgentTurnRecord SingleTurn(IReadOnlyList<AgentTurnRecord> turns)
    {
        return Assert.Single(turns);
    }

    private static async Task<IReadOnlyList<AgentTurnRecord>> WaitForTurnStatusAsync(
        IAgentSessionGrain session,
        string turnId,
        AgentTurnStatus expected,
        TimeSpan timeout)
    {
        return await TestWait.ForAsync(
            () => session.ListTurnsAsync(),
            turns =>
            {
                var match = turns.FirstOrDefault(turn =>
                    string.Equals(turn.Id, turnId, StringComparison.Ordinal));
                return match is not null && match.Status == expected;
            },
            timeout,
            TimeSpan.FromMilliseconds(25),
            $"turn {turnId} reaches {expected}");
    }

    [Fact]
    public async Task FollowupIdleStart_RecordsSingleQueuedTurn_LinkedToAcceptedSessionInput_BeforeDispatch()
    {
        var sessionId = $"session-522-followup-{Guid.NewGuid():N}";
        var projectId = $"project-522-{Guid.NewGuid():N}";
        var session = await OpenIdleSessionAsync(sessionId, projectId);
        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(6));

        var inputId = $"input-{Guid.NewGuid():N}";
        var turnId = $"turn-{Guid.NewGuid():N}";

        await session.RecordFollowupTurnAsync(new RecordFollowupTurnCommand(
            InputId: inputId,
            TurnId: turnId,
            Prompt: "follow up please",
            Source: "generic-followup"));

        var snapshot = await session.GetInitialLaunchAsync();
        Assert.NotNull(snapshot);
        Assert.NotNull(snapshot!.Input);
        Assert.Equal(inputId, snapshot.Input!.Id);
        Assert.Equal(AgentSessionInputAcceptance.Accepted, snapshot.Input.Acceptance);
        Assert.Null(snapshot.Input.JobId);

        var turns = await session.ListTurnsAsync();
        var turn = SingleTurn(turns);
        Assert.Equal(turnId, turn.Id);
        Assert.Equal(AgentTurnStatus.Queued, turn.Status);
        Assert.Null(turn.JobId);
        Assert.Equal(new[] { inputId }, turn.InputIds);
    }

    [Fact]
    public async Task FollowupIdleStart_RedispatchWithSameIds_IsIdempotentAndSurvivesRestart()
    {
        var sessionId = $"session-522-idempotent-{Guid.NewGuid():N}";
        var projectId = $"project-522-{Guid.NewGuid():N}";
        var session = await OpenIdleSessionAsync(sessionId, projectId);
        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(6));

        var inputId = $"input-{Guid.NewGuid():N}";
        var turnId = $"turn-{Guid.NewGuid():N}";
        var command = new RecordFollowupTurnCommand(
            InputId: inputId,
            TurnId: turnId,
            Prompt: "follow up please",
            Source: "generic-followup");

        await session.RecordFollowupTurnAsync(command);
        await session.RecordFollowupTurnAsync(command);

        var turns = await session.ListTurnsAsync();
        var turn = SingleTurn(turns);
        Assert.Equal(turnId, turn.Id);
        Assert.Equal(AgentTurnStatus.Queued, turn.Status);

        await session.AsReference<IGrainManagementExtension>().DeactivateOnIdle();
        await session.GetAsync();
        var turnsAfter = await session.ListTurnsAsync();
        var turnAfter = SingleTurn(turnsAfter);
        Assert.Equal(turnId, turnAfter.Id);
        Assert.Equal(AgentTurnStatus.Queued, turnAfter.Status);
    }

    [Fact]
    public async Task MarkTurnExecuting_IsKeyedByTurnId_AndAdvancesOnlyMatchingTurn()
    {
        var sessionId = $"session-522-execute-{Guid.NewGuid():N}";
        var projectId = $"project-522-{Guid.NewGuid():N}";
        var session = await OpenIdleSessionAsync(sessionId, projectId);
        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(6));

        var inputId = $"input-{Guid.NewGuid():N}";
        var turnId = $"turn-{Guid.NewGuid():N}";
        await session.RecordFollowupTurnAsync(new RecordFollowupTurnCommand(
            InputId: inputId,
            TurnId: turnId,
            Prompt: "follow up please",
            Source: "generic-followup"));

        await session.MarkTurnExecutingAsync(turnId);

        var turns = await session.ListTurnsAsync();
        var turn = SingleTurn(turns);
        Assert.Equal(AgentTurnStatus.Executing, turn.Status);
    }

    [Fact]
    public async Task MarkTurnTerminal_IsKeyedByTurnId_AndAdvancesOnlyMatchingTurn()
    {
        var sessionId = $"session-522-terminal-{Guid.NewGuid():N}";
        var projectId = $"project-522-{Guid.NewGuid():N}";
        var session = await OpenIdleSessionAsync(sessionId, projectId);
        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(6));

        var inputId = $"input-{Guid.NewGuid():N}";
        var turnId = $"turn-{Guid.NewGuid():N}";
        await session.RecordFollowupTurnAsync(new RecordFollowupTurnCommand(
            InputId: inputId,
            TurnId: turnId,
            Prompt: "follow up please",
            Source: "generic-followup"));
        await session.MarkTurnExecutingAsync(turnId);

        await session.MarkTurnTerminalAsync(
            turnId,
            AgentTurnStatus.Completed,
            new AgentTurnResult(Message: "ok", Output: "{}"));

        var turns = await session.ListTurnsAsync();
        var turn = SingleTurn(turns);
        Assert.Equal(AgentTurnStatus.Completed, turn.Status);
        Assert.NotNull(turn.Result);
        Assert.Equal("ok", turn.Result!.Message);
    }

    [Fact]
    public async Task CancelTurn_QueuedTurnFlipsToCancelled_AndConvergesActivityToIdle()
    {
        var sessionId = $"session-522-cancel-{Guid.NewGuid():N}";
        var projectId = $"project-522-{Guid.NewGuid():N}";
        var session = await OpenIdleSessionAsync(sessionId, projectId);
        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(6));

        var inputId = $"input-{Guid.NewGuid():N}";
        var turnId = $"turn-{Guid.NewGuid():N}";
        await session.RecordFollowupTurnAsync(new RecordFollowupTurnCommand(
            InputId: inputId,
            TurnId: turnId,
            Prompt: "follow up please",
            Source: "generic-followup"));

        await session.CancelTurnAsync(turnId);

        var turns = await session.ListTurnsAsync();
        var turn = SingleTurn(turns);
        Assert.Equal(AgentTurnStatus.Cancelled, turn.Status);

        var info = await session.GetAsync();
        Assert.NotNull(info);
        Assert.Equal("idle", info!.Status);
    }

    [Fact]
    public async Task CancelTurn_NoOpOnAlreadyTerminalTurn()
    {
        var sessionId = $"session-522-cancelterminal-{Guid.NewGuid():N}";
        var projectId = $"project-522-{Guid.NewGuid():N}";
        var session = await OpenIdleSessionAsync(sessionId, projectId);
        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(6));

        var inputId = $"input-{Guid.NewGuid():N}";
        var turnId = $"turn-{Guid.NewGuid():N}";
        await session.RecordFollowupTurnAsync(new RecordFollowupTurnCommand(
            InputId: inputId,
            TurnId: turnId,
            Prompt: "follow up please",
            Source: "generic-followup"));
        await session.MarkTurnExecutingAsync(turnId);
        await session.MarkTurnTerminalAsync(turnId, AgentTurnStatus.Completed, null);

        await session.CancelTurnAsync(turnId);

        var turns = await session.ListTurnsAsync();
        var turn = SingleTurn(turns);
        Assert.Equal(AgentTurnStatus.Completed, turn.Status);
    }

    [Fact]
    public async Task CancelTurn_NoOpOnExecutingTurn()
    {
        var sessionId = $"session-522-cancelexec-{Guid.NewGuid():N}";
        var projectId = $"project-522-{Guid.NewGuid():N}";
        var session = await OpenIdleSessionAsync(sessionId, projectId);
        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(6));

        var inputId = $"input-{Guid.NewGuid():N}";
        var turnId = $"turn-{Guid.NewGuid():N}";
        await session.RecordFollowupTurnAsync(new RecordFollowupTurnCommand(
            InputId: inputId,
            TurnId: turnId,
            Prompt: "follow up please",
            Source: "generic-followup"));
        await session.MarkTurnExecutingAsync(turnId);

        await session.CancelTurnAsync(turnId);

        var turns = await session.ListTurnsAsync();
        var turn = SingleTurn(turns);
        Assert.Equal(AgentTurnStatus.Executing, turn.Status);
    }

    [Fact]
    public async Task ResolveTurnControl_ReturnsClassificationForKnownAndUnknownIds()
    {
        var sessionId = $"session-522-resolve-{Guid.NewGuid():N}";
        var projectId = $"project-522-{Guid.NewGuid():N}";
        var session = await OpenIdleSessionAsync(sessionId, projectId);
        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(6));

        var inputId = $"input-{Guid.NewGuid():N}";
        var turnId = $"turn-{Guid.NewGuid():N}";
        await session.RecordFollowupTurnAsync(new RecordFollowupTurnCommand(
            InputId: inputId,
            TurnId: turnId,
            Prompt: "follow up please",
            Source: "generic-followup"));

        var queued = await session.ResolveTurnControlAsync(turnId);
        Assert.NotNull(queued);
        Assert.Equal(AgentTurnStatus.Queued, queued!.Status);
        Assert.Equal(AgentTurnControlClassification.Queued, queued.Classification);
        Assert.False(queued.IsLaunchTurn);

        await session.MarkTurnExecutingAsync(turnId);
        var executing = await session.ResolveTurnControlAsync(turnId);
        Assert.NotNull(executing);
        Assert.Equal(AgentTurnStatus.Executing, executing!.Status);
        Assert.Equal(AgentTurnControlClassification.Executing, executing.Classification);

        await session.MarkTurnTerminalAsync(turnId, AgentTurnStatus.Failed, null);
        var terminal = await session.ResolveTurnControlAsync(turnId);
        Assert.NotNull(terminal);
        Assert.Equal(AgentTurnStatus.Failed, terminal!.Status);
        Assert.Equal(AgentTurnControlClassification.Terminal, terminal.Classification);

        var unknown = await session.ResolveTurnControlAsync("turn-does-not-exist");
        Assert.Null(unknown);
    }

    [Fact]
    public async Task SessionInputEvent_DrivesNonLaunchTurnFromQueuedToExecuting()
    {
        var sessionId = $"session-522-sessioninput-{Guid.NewGuid():N}";
        var projectId = $"project-522-{Guid.NewGuid():N}";
        var session = await OpenIdleSessionAsync(sessionId, projectId);
        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(6));

        var inputId = $"input-{Guid.NewGuid():N}";
        var turnId = $"turn-{Guid.NewGuid():N}";
        await session.RecordFollowupTurnAsync(new RecordFollowupTurnCommand(
            InputId: inputId,
            TurnId: turnId,
            Prompt: "follow up please",
            Source: "generic-followup"));

        await session.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new[]
            {
                new AgentSessionRuntimeEventInput(
                    RuntimeEventTypes.SessionInput,
                    $$"""{"text":"follow up please","kind":"followup","inputId":"{{inputId}}","turnId":"{{turnId}}","source":"followup"}""")
            },
            "runtime-1"));

        var turns = await session.ListTurnsAsync();
        var turn = SingleTurn(turns);
        Assert.Equal(AgentTurnStatus.Executing, turn.Status);
    }

    [Fact]
    public async Task TerminalSessionActivity_DrivesNonLaunchTurnToCompleted()
    {
        var sessionId = $"session-522-terminal-activity-{Guid.NewGuid():N}";
        var projectId = $"project-522-{Guid.NewGuid():N}";
        var session = await OpenIdleSessionAsync(sessionId, projectId);
        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(6));

        var inputId = $"input-{Guid.NewGuid():N}";
        var turnId = $"turn-{Guid.NewGuid():N}";
        await session.RecordFollowupTurnAsync(new RecordFollowupTurnCommand(
            InputId: inputId,
            TurnId: turnId,
            Prompt: "follow up please",
            Source: "generic-followup"));
        await session.MarkTurnExecutingAsync(turnId);

        await session.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new[]
            {
                new AgentSessionRuntimeEventInput(
                    RuntimeEventTypes.SessionActivity,
                    $$"""{"activity":"idle","status":"completed","operationId":"op","turnId":"{{turnId}}","source":"followup"}""")
            },
            "runtime-1"));

        var turns = await session.ListTurnsAsync();
        var turn = SingleTurn(turns);
        Assert.Equal(AgentTurnStatus.Completed, turn.Status);
    }

    [Fact]
    public async Task TerminalSessionActivity_UnknownDrivesNonLaunchTurnToUnknown()
    {
        var sessionId = $"session-522-unknown-activity-{Guid.NewGuid():N}";
        var projectId = $"project-522-{Guid.NewGuid():N}";
        var session = await OpenIdleSessionAsync(sessionId, projectId);
        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(6));

        var inputId = $"input-{Guid.NewGuid():N}";
        var turnId = $"turn-{Guid.NewGuid():N}";
        await session.RecordFollowupTurnAsync(new RecordFollowupTurnCommand(
            InputId: inputId,
            TurnId: turnId,
            Prompt: "follow up please",
            Source: "generic-followup"));
        await session.MarkTurnExecutingAsync(turnId);

        await session.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new[]
            {
                new AgentSessionRuntimeEventInput(
                    RuntimeEventTypes.SessionActivity,
                    $$"""{"activity":"unknown","status":"failed","operationId":"op","turnId":"{{turnId}}","source":"cancel"}""")
            },
            "runtime-1"));

        var turns = await session.ListTurnsAsync();
        var turn = SingleTurn(turns);
        Assert.Equal(AgentTurnStatus.Unknown, turn.Status);
    }

    [Fact]
    public async Task SessionInputEvent_OnAlreadyTerminalTurn_IsNoOp()
    {
        var sessionId = $"session-522-noopterminal-{Guid.NewGuid():N}";
        var projectId = $"project-522-{Guid.NewGuid():N}";
        var session = await OpenIdleSessionAsync(sessionId, projectId);
        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(6));

        var inputId = $"input-{Guid.NewGuid():N}";
        var turnId = $"turn-{Guid.NewGuid():N}";
        await session.RecordFollowupTurnAsync(new RecordFollowupTurnCommand(
            InputId: inputId,
            TurnId: turnId,
            Prompt: "follow up please",
            Source: "generic-followup"));
        await session.MarkTurnExecutingAsync(turnId);
        await session.MarkTurnTerminalAsync(turnId, AgentTurnStatus.Completed, null);

        await session.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new[]
            {
                new AgentSessionRuntimeEventInput(
                    RuntimeEventTypes.SessionInput,
                    $$"""{"text":"follow up please","kind":"followup","inputId":"{{inputId}}","turnId":"{{turnId}}","source":"followup"}""")
            },
            "runtime-1"));

        var turns = await session.ListTurnsAsync();
        var turn = SingleTurn(turns);
        Assert.Equal(AgentTurnStatus.Completed, turn.Status);
    }

    [Fact]
    public async Task LaunchTurn_IsNotPromotedBySessionInputEvent()
    {
        var sessionId = $"session-522-launch-input-{Guid.NewGuid():N}";
        var projectId = $"project-522-{Guid.NewGuid():N}";
        var session = await OpenIdleSessionAsync(sessionId, projectId);

        var inputId = $"input-{Guid.NewGuid():N}";
        var turnId = $"turn-{Guid.NewGuid():N}";
        var jobId = $"job-522-launch-input-{Guid.NewGuid():N}";
        await session.EnsureInitialLaunchAsync(new EnsureInitialLaunchCommand(
            InputId: inputId,
            TurnId: turnId,
            Prompt: "launch prompt",
            Source: "agent-launch",
            JobId: jobId,
            Metadata: new AgentSessionMetadata(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
                    [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
                    [GenericAgentSessionMetadata.AgentId] = "agent-test",
                }),
            Runtime: "opencode",
            WorkDir: "/tmp/turn-522"));

        await session.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new[]
            {
                new AgentSessionRuntimeEventInput(
                    RuntimeEventTypes.SessionInput,
                    $$"""{"text":"launch prompt","kind":"task","inputId":"{{inputId}}","turnId":"{{turnId}}"}""")
            },
            "runtime-1"));

        var turns = await session.ListTurnsAsync();
        var turn = SingleTurn(turns);
        Assert.Equal(AgentTurnStatus.Queued, turn.Status);
        Assert.Equal(jobId, turn.JobId);
    }

    [Fact]
    public async Task LaunchTurn_IsNotTerminalisedByAgentJobAppendTerminalCloseSessionActivity()
    {
        var sessionId = $"session-522-launch-isolation-{Guid.NewGuid():N}";
        var projectId = $"project-522-{Guid.NewGuid():N}";
        var session = await OpenIdleSessionAsync(sessionId, projectId);

        var inputId = $"input-{Guid.NewGuid():N}";
        var turnId = $"turn-{Guid.NewGuid():N}";
        var jobId = $"job-522-launch-isolation-{Guid.NewGuid():N}";
        await session.EnsureInitialLaunchAsync(new EnsureInitialLaunchCommand(
            InputId: inputId,
            TurnId: turnId,
            Prompt: "launch prompt",
            Source: "agent-launch",
            JobId: jobId,
            Metadata: new AgentSessionMetadata(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
                    [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
                    [GenericAgentSessionMetadata.AgentId] = "agent-test",
                }),
            Runtime: "opencode",
            WorkDir: "/tmp/turn-522"));

        await session.AppendTerminalCloseAsync(new AppendTerminalCloseCommand(
            SessionId: sessionId,
            DeliveryId: AgentJobSessionDeliveryIds.TerminalDeliveryId(jobId),
            Status: "completed",
            ExitCode: 0,
            FailureReason: null,
            FailureCategory: null,
            RecordedAt: _fixture.TimeProvider.GetUtcNow(),
            PayloadJson: JsonSerializer.Serialize(new
            {
                agentJobId = jobId,
            }),
            RuntimeSessionId: "runtime-1"));

        var turns = await session.ListTurnsAsync();
        var turn = SingleTurn(turns);
        Assert.Equal(AgentTurnStatus.Queued, turn.Status);
        Assert.Equal(jobId, turn.JobId);
        Assert.Null(turn.Result);

        await session.MarkInitialTurnTerminalAsync(
            jobId,
            AgentTurnStatus.Completed,
            new AgentTurnResult(
                Message: "rich verdict",
                Output: "{\"ok\":true}",
                FailureReason: null,
                FailureCategory: null,
                ExitCode: 0));

        var afterAuthoritative = await session.ListTurnsAsync();
        var terminalTurn = SingleTurn(afterAuthoritative);
        Assert.Equal(AgentTurnStatus.Completed, terminalTurn.Status);
        Assert.NotNull(terminalTurn.Result);
        Assert.Equal("rich verdict", terminalTurn.Result!.Message);
        Assert.Equal("{\"ok\":true}", terminalTurn.Result.Output);
        Assert.Equal(0, terminalTurn.Result.ExitCode);
    }

    [Fact]
    public async Task AgentJobLaunchPath_DelegatesToTurnIdKeyedTransitions()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync(
            $"agent-job-522-delegate-{Guid.NewGuid():N}");
        var sessionId = $"session-522-delegate-{Guid.NewGuid():N}";
        var jobKey = $"agent-job-522-delegate-{Guid.NewGuid():N}";
        var inputId = $"input-{Guid.NewGuid():N}";
        var turnId = $"turn-{Guid.NewGuid():N}";

        var session = Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.OpenAsync(new OpenAgentSessionCommand(
            RunnerId: runnerId,
            AgentRuntime: "opencode",
            WorkDir: "/tmp/turn-522",
            Metadata: new AgentSessionMetadata(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
                    [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
                    [GenericAgentSessionMetadata.AgentId] = "agent-test",
                })));
        await session.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(
            AgentSessionId: "runtime-1",
            WorkDir: "/tmp/turn-522"));

        var job = Grains.GetGrain<IAgentJobGrain>(jobKey);

        await session.EnsureInitialLaunchAsync(new EnsureInitialLaunchCommand(
            InputId: inputId,
            TurnId: turnId,
            Prompt: "do the thing",
            Source: "agent-launch",
            JobId: jobKey,
            Metadata: new AgentSessionMetadata(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
                    [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
                    [GenericAgentSessionMetadata.AgentId] = "agent-test",
                }),
            Runtime: "opencode",
            WorkDir: "/tmp/turn-522"));

        await job.SubmitAsync(new AgentJobInput(
            Prompt: "do the thing",
            WorkspacePath: "/tmp/turn-522",
            ProjectId: projectId,
            AgentSessionId: sessionId,
            AgentId: "agent-test",
            InitialInputId: inputId,
            InitialTurnId: turnId));

        await WaitForRunningAsync(job);

        var turns = await session.ListTurnsAsync();
        var turn = SingleTurn(turns);
        Assert.Equal(AgentTurnStatus.Queued, turn.Status);
        Assert.Equal(jobKey, turn.JobId);

        var workId = (await job.GetRuntimeSnapshotAsync()).CurrentWorkId!;
        await job.ReportResultAsync(
            runnerId,
            workId: workId,
            result: new WorkResult(
                Status: "completed",
                Message: "rich verdict",
                Output: JSON.DeserializeElement("{\"ok\":true}"),
                ExitCode: 0));
        await WaitForStatusAsync(job, AgentJobStatus.Completed, TimeSpan.FromSeconds(5));

        var after = await session.ListTurnsAsync();
        var finalTurn = SingleTurn(after);
        Assert.Equal(AgentTurnStatus.Completed, finalTurn.Status);
        Assert.NotNull(finalTurn.Result);
        Assert.Equal("rich verdict", finalTurn.Result!.Message);
        Assert.Equal(0, finalTurn.Result.ExitCode);
    }

    [Fact]
    public async Task CancelTurn_IsPersistedAcrossDeactivation()
    {
        var sessionId = $"session-522-cancelrestart-{Guid.NewGuid():N}";
        var projectId = $"project-522-{Guid.NewGuid():N}";
        var session = await OpenIdleSessionAsync(sessionId, projectId);
        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(6));

        var inputId = $"input-{Guid.NewGuid():N}";
        var turnId = $"turn-{Guid.NewGuid():N}";
        await session.RecordFollowupTurnAsync(new RecordFollowupTurnCommand(
            InputId: inputId,
            TurnId: turnId,
            Prompt: "follow up please",
            Source: "generic-followup"));

        await session.CancelTurnAsync(turnId);

        await session.AsReference<IGrainManagementExtension>().DeactivateOnIdle();
        await session.GetAsync();

        var after = await session.ListTurnsAsync();
        var turn = SingleTurn(after);
        Assert.Equal(turnId, turn.Id);
        Assert.Equal(AgentTurnStatus.Cancelled, turn.Status);
    }
}
