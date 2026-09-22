using Mohist.Server.Sessions.Domain;
using Xunit;

namespace Mohist.Server.Tests.Sessions;

[Trait("level", "L0")]
public sealed class AgentSessionRecoveryDomainTests
{
    [Fact]
    public void RebindRuntimeSession_ReplacesFullBindingAndClearsRuntimeContext()
    {
        var session = CreateSession();
        session.Status = session.Status with
        {
            AgentRuntimeSessionId = "runtime-old",
            UsageSummary = new AgentUsageSummary { InputTokens = 100, ContextWindowUsed = 90_000, ContextWindowSize = 200_000 }
        };
        var expected = session.CurrentRuntimeBinding();

        var events = session.RebindRuntimeSession(
            expected,
            new AgentRuntimeBinding("runner-2", "pi", "runtime-new"),
            "runtime-change",
            TestTime.UtcDateTime);

        Assert.Equal(new AgentRuntimeBinding("runner-2", "pi", "runtime-new"), session.CurrentRuntimeBinding());
        Assert.Equal(100, session.Status.UsageSummary!.InputTokens);
        Assert.Null(session.Status.UsageSummary.ContextWindowUsed);
        Assert.Null(session.Status.UsageSummary.ContextWindowSize);
        Assert.IsType<AgentSessionRuntimeBound>(Assert.Single(events).Value);
    }

    [Fact]
    public void MissingRecovery_RetargetsOnlyExecutionFactsForTheSealedQueuedTurn()
    {
        var session = CreateSession();
        session.AttachPhysicalSession("runtime-old", null, "/work", null, null, TestTime.UtcDateTime);
        var accepted = session.AcceptFollowup(
            "input-1", "turn-1", "operation-1", "continue", "agent-session-followup", "key-1", TestTime.UtcDateTime);
        var turn = Assert.Single(session.Status.Turns!);
        session.Status = session.Status with
        {
            Turns = [turn with
            {
                WorkflowExecution = new SessionWorkflowExecutionBinding(
                    "delivery-1", "run-1", "attempt-1", "work-1", "runner-1",
                    session.Id, turn.Id, "opencode", "runtime-old"),
            }],
            PendingFollowups = session.Status.PendingFollowups!
                .Select(lease => lease with { Dispatching = true, PayloadSealed = true })
                .ToArray(),
        };

        session.RebindRuntimeSession(
            session.CurrentRuntimeBinding(),
            new AgentRuntimeBinding("runner-1", "pi", "runtime-new"),
            "missing-recovery",
            TestTime.UtcDateTime.AddMinutes(1),
            session.BindingEpoch,
            accepted.TurnId);

        var input = Assert.Single(session.Status.Inputs!);
        var retargetedTurn = Assert.Single(session.Status.Turns!);
        var lease = Assert.Single(session.Status.PendingFollowups!);
        Assert.Equal(1, input.ContextGeneration);
        Assert.Equal(2, retargetedTurn.ContextGeneration);
        Assert.Equal("input-1", input.Id);
        Assert.Equal("turn-1", retargetedTurn.Id);
        Assert.Equal("operation-1", retargetedTurn.OperationId);
        Assert.Equal("runtime-new", lease.RuntimeSessionId);
        Assert.Equal("delivery-1", retargetedTurn.WorkflowExecution!.InputDeliveryId);
        Assert.Equal("attempt-1", retargetedTurn.WorkflowExecution.ActionAttemptId);
        Assert.Equal("pi", retargetedTurn.WorkflowExecution.Runtime);
        Assert.Equal("runtime-new", retargetedTurn.WorkflowExecution.RuntimeSessionId);
        Assert.Equal(AgentSessionActivity.Active, session.Status.Activity);
    }

    [Fact]
    public void RuntimeChangeAttach_CannotStrandAnAcceptedQueuedTurn()
    {
        var session = CreateSession();
        session.AttachPhysicalSession("runtime-old", "original-model", "/work", null, null, TestTime.UtcDateTime);
        var accepted = session.AcceptFollowup(
            "input-1", "turn-1", "operation-1", "continue", "agent-session-followup", "key-1", TestTime.UtcDateTime);
        var bindingBefore = session.CurrentRuntimeBinding();
        var generationBefore = session.Status.ContextGeneration;

        Assert.Throws<InvalidOperationException>(() => session.AttachPhysicalSession(
            "runtime-new", "new-model", "/work", null, null, TestTime.UtcDateTime.AddMinutes(1),
            runtime: "pi"));

        Assert.Equal(bindingBefore, session.CurrentRuntimeBinding());
        Assert.Equal("original-model", session.Settings.Model);
        Assert.Equal(generationBefore, session.Status.ContextGeneration);
        Assert.Equal(generationBefore,
            Assert.Single(session.Status.Turns!, turn => turn.Id == accepted.TurnId).ContextGeneration);
        Assert.Equal("continue", Assert.Single(session.Status.Inputs!).Text);
    }

    [Fact]
    public void FirstPhysicalAttach_PreservesAcceptedInitialGeneration()
    {
        var session = CreateSession();
        session.EnsureInitialLaunch(
            "input-1", "turn-1", "prompt", "agent-connection", "job-1", TestTime.UtcDateTime);

        session.AttachPhysicalSession(
            "runtime-first", null, "/work", null, null, TestTime.UtcDateTime.AddMinutes(1));

        Assert.Equal(1, session.Status.ContextGeneration);
        Assert.Equal(1, Assert.Single(session.Status.Inputs!).ContextGeneration);
        Assert.Equal(1, Assert.Single(session.Status.Turns!).ContextGeneration);
    }

    [Fact]
    public void RebindRuntimeSession_RejectsStaleExpectedBindingWithoutMutation()
    {
        var session = CreateSession();
        session.Status = session.Status with { AgentRuntimeSessionId = "runtime-current" };
        var before = session.CurrentRuntimeBinding();

        Assert.Throws<StaleRuntimeSessionBindingException>(() => session.RebindRuntimeSession(
            new AgentRuntimeBinding("runner-1", "opencode", "runtime-stale"),
            new AgentRuntimeBinding("runner-1", "opencode", "runtime-new"),
            "reset",
            TestTime.UtcDateTime));

        Assert.Equal(before, session.CurrentRuntimeBinding());
    }

    [Theory]
    [InlineData(AgentTurnStatus.Executing)]
    [InlineData(AgentTurnStatus.Unknown)]
    public void RebindRuntimeSession_RejectsCurrentExecutionFactsWithoutMutation(AgentTurnStatus status)
    {
        var session = CreateSession();
        session.AttachPhysicalSession("runtime-current", null, "/work", null, null, TestTime.UtcDateTime);
        session.EnsureInitialLaunch(
            "input-1", "turn-1", "prompt", "agent-connection", "job-1", TestTime.UtcDateTime);
        if (status == AgentTurnStatus.Executing)
            session.MarkInitialTurnExecuting("job-1", TestTime.UtcDateTime);
        else
            session.MarkInitialTurnTerminal("job-1", AgentTurnStatus.Unknown, null, TestTime.UtcDateTime);
        session.Status = session.Status with { Activity = AgentSessionActivity.Idle };
        var statusBefore = session.Status;
        var bindingBefore = session.CurrentRuntimeBinding();
        var epochBefore = session.BindingEpoch;

        Assert.Throws<InvalidOperationException>(() => session.RebindRuntimeSession(
            bindingBefore,
            new AgentRuntimeBinding("runner-1", "opencode", "runtime-candidate"),
            "missing-recovery",
            TestTime.UtcDateTime.AddMinutes(1),
            epochBefore));

        Assert.Equal(statusBefore, session.Status);
        Assert.Equal(bindingBefore, session.CurrentRuntimeBinding());
        Assert.Equal(epochBefore, session.BindingEpoch);
    }

    [Theory]
    [InlineData("followup")]
    [InlineData("reset")]
    [InlineData("stop")]
    public void RebindRuntimeSession_RejectsActiveOperationsWithoutMutation(string operation)
    {
        var session = CreateSession();
        session.AttachPhysicalSession("runtime-current", null, "/work", null, null, TestTime.UtcDateTime);
        session.Status = operation switch
        {
            "followup" => session.Status with
            {
                PendingFollowups = [new AgentSessionFollowupLease("followup-1", "runtime-current")],
            },
            "reset" => session.Status with
            {
                PendingReset = new AgentSessionResetReservation(
                    "reset-1", "runtime-current", "opencode", TestTime.UtcDateTime),
            },
            _ => session.Status with
            {
                PendingStop = new AgentSessionStopClaim("turn-1", "stop-1"),
            },
        };
        var statusBefore = session.Status;
        var bindingBefore = session.CurrentRuntimeBinding();
        var epochBefore = session.BindingEpoch;

        Assert.Throws<InvalidOperationException>(() => session.RebindRuntimeSession(
            bindingBefore,
            new AgentRuntimeBinding("runner-1", "opencode", "runtime-candidate"),
            "missing-recovery",
            TestTime.UtcDateTime.AddMinutes(1),
            epochBefore));

        Assert.Equal(statusBefore, session.Status);
        Assert.Equal(bindingBefore, session.CurrentRuntimeBinding());
        Assert.Equal(epochBefore, session.BindingEpoch);
    }

    [Theory]
    [InlineData(AgentSessionActivity.Active)]
    [InlineData(AgentSessionActivity.Unknown)]
    public void RebindRuntimeSession_RequiresIdle(AgentSessionActivity activity)
    {
        var session = CreateSession();
        session.Status = session.Status with { AgentRuntimeSessionId = "runtime-current", Activity = activity };

        Assert.Throws<InvalidOperationException>(() => session.RebindRuntimeSession(
            session.CurrentRuntimeBinding(),
            new AgentRuntimeBinding("runner-1", "opencode", "runtime-new"),
            "reset",
            TestTime.UtcDateTime));
    }

    [Fact]
    public void RebindRuntimeSession_RejectsUnknownReason()
    {
        var session = CreateSession();

        Assert.Throws<ArgumentOutOfRangeException>(() => session.RebindRuntimeSession(
            session.CurrentRuntimeBinding(),
            new AgentRuntimeBinding("runner-1", "opencode", "runtime-new"),
            "other",
            TestTime.UtcDateTime));
    }

    [Theory]
    [InlineData(null, "runtime-1", true)]
    [InlineData("acp", "runtime-1", true)]
    [InlineData("opencode", null, true)]
    [InlineData("opencode", "runtime-1", false)]
    [InlineData("pi", "runtime-1", false)]
    public void IsRuntimeSessionMissing_RequiresARegisteredRuntimeAndPhysicalBinding(
        string? runtime,
        string? runtimeSessionId,
        bool expected)
    {
        var session = CreateSession();
        session.Runtime = session.Runtime with { Runtime = runtime };
        session.Status = session.Status with { AgentRuntimeSessionId = runtimeSessionId };

        var missing = session.IsRuntimeSessionMissing(
            candidate => candidate is "opencode" or "pi");

        Assert.Equal(expected, missing);
    }

    private static AgentSession CreateSession() => AgentSession.Create(
        "session-1",
        "runner-1",
        "/work",
        metadata: new AgentSessionMetadata()
            .WithLabel("mohist.io/project-id", "project-1")
            .WithLabel("mohist.io/source-kind", "workflow")
            .WithLabel("mohist.io/source-id", "workflow-1")
            .WithLabel("mohist.io/session-name", "build"),
        now: TestTime.UtcDateTime,
        runtime: "opencode");
}
