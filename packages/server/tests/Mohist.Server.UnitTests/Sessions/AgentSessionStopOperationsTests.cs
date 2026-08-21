using System.Reflection;
using Mohist.Server.Api;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Orleans;
using Xunit;

namespace Mohist.Server.UnitTests.Sessions;

public sealed class AgentSessionStopOperationsTests
{
    [Fact]
    public async Task Missing_turn_is_reported_without_delivery()
    {
        var context = Context(new AgentTurnStopClaimResult(null, false, null));

        var result = await context.StopAsync();

        Assert.Equal(TurnControlResultKind.NotFound, result.Kind);
        Assert.Null(context.Delivery.Request);
    }

    [Fact]
    public async Task Queued_followup_is_cancelled_locally()
    {
        var queued = Control(AgentTurnStatus.Queued, AgentTurnControlClassification.Queued);
        var context = Context(
            new AgentTurnStopClaimResult(queued, false, null),
            new AgentTurnStopResult(Control(AgentTurnStatus.Cancelled, AgentTurnControlClassification.Terminal), true));

        var result = await context.StopAsync();

        Assert.Equal(TurnControlResultKind.Cancelled, result.Kind);
        Assert.Equal(AgentTurnStatus.Cancelled, result.Status);
        Assert.Null(context.Delivery.Request);
    }

    [Fact]
    public async Task Terminal_turn_without_an_active_claim_is_already_ended()
    {
        var terminal = Control(AgentTurnStatus.Completed, AgentTurnControlClassification.Terminal);
        var context = Context(new AgentTurnStopClaimResult(terminal, false, null));

        var result = await context.StopAsync();

        Assert.Equal(TurnControlResultKind.AlreadyEnded, result.Kind);
        Assert.Equal(AgentTurnStatus.Completed, result.Status);
        Assert.Null(context.Delivery.Request);
    }

    [Fact]
    public async Task Executing_turn_without_dispatch_ownership_remains_requested()
    {
        var executing = Control(AgentTurnStatus.Executing, AgentTurnControlClassification.Executing);
        var context = Context(new AgentTurnStopClaimResult(executing, false, null));

        var result = await context.StopAsync();

        Assert.Equal(TurnControlResultKind.StopRequested, result.Kind);
        Assert.Null(context.Delivery.Request);
    }

    [Theory]
    [InlineData("stopped", true, "Stopped", AgentTurnStatus.Cancelled, AgentSessionStopDisposition.Stopped)]
    [InlineData("unknown", true, "Unknown", AgentTurnStatus.Unknown, AgentSessionStopDisposition.Unknown)]
    [InlineData("not-cancellable", false, "NotCancellable", AgentTurnStatus.Executing, AgentSessionStopDisposition.NotCancellable)]
    [InlineData("ended", false, "AlreadyEnded", AgentTurnStatus.Executing, AgentSessionStopDisposition.Ended)]
    [InlineData("other", false, "StopRequested", AgentTurnStatus.Executing, AgentSessionStopDisposition.StopRequested)]
    public async Task Runner_reply_is_mapped_and_settled(
        string reply,
        bool interruptUnconfirmed,
        string expectedKind,
        AgentTurnStatus expectedStatus,
        AgentSessionStopDisposition expectedDisposition)
    {
        var executing = Control(AgentTurnStatus.Executing, AgentTurnControlClassification.Executing);
        var context = Context(
            new AgentTurnStopClaimResult(executing, true, "operation-1"),
            delivery: new SessionStopDeliveryResponse(new RunnerStopReply(reply, interruptUnconfirmed), true));

        var result = await context.StopAsync();

        Assert.Equal(Enum.Parse<TurnControlResultKind>(expectedKind), result.Kind);
        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(interruptUnconfirmed, result.InterruptUnconfirmed);
        Assert.True(result.DispatchStarted);
        Assert.Equal("turn-1", context.Delivery.Request?.TurnId);
        Assert.Equal("operation-1", context.Delivery.Request?.OperationId);
        Assert.Equal(("turn-1", "operation-1"), context.Session.MarkedDispatched);
        Assert.Equal(("turn-1", "operation-1", expectedDisposition), context.Session.AppliedDelivery);
    }

    [Fact]
    public async Task Missing_runner_reply_keeps_the_claim_for_recovery()
    {
        var executing = Control(AgentTurnStatus.Executing, AgentTurnControlClassification.Executing);
        var context = Context(
            new AgentTurnStopClaimResult(executing, true, "operation-1"),
            delivery: new SessionStopDeliveryResponse(null, true));

        var result = await context.StopAsync();

        Assert.Equal(TurnControlResultKind.RunnerUnavailable, result.Kind);
        Assert.True(result.DispatchStarted);
        Assert.Equal(("turn-1", "operation-1"), context.Session.MarkedDispatched);
        Assert.Null(context.Session.AppliedDelivery);
    }

    [Fact]
    public async Task Executing_launch_stop_does_not_arbitrate_the_job_verdict()
    {
        var executing = new AgentTurnControlState(
            "turn-1",
            AgentTurnStatus.Executing,
            AgentTurnControlClassification.Executing,
            IsLaunchTurn: true,
            JobId: "job-1");
        var context = Context(
            new AgentTurnStopClaimResult(executing, true, "operation-1"),
            delivery: new SessionStopDeliveryResponse(new RunnerStopReply("stopped"), true));

        var result = await context.StopAsync();

        Assert.Equal(TurnControlResultKind.Stopped, result.Kind);
        Assert.Equal(AgentSessionStopDisposition.Stopped, context.Session.AppliedDelivery?.Disposition);
    }

    private static StopContext Context(
        AgentTurnStopClaimResult claim,
        AgentTurnStopResult? queued = null,
        SessionStopDeliveryResponse? delivery = null)
    {
        var session = DispatchProxy.Create<IAgentSessionGrain, SessionGrainProxy>();
        var sessionProxy = (SessionGrainProxy)(object)session;
        sessionProxy.Claim = claim;
        sessionProxy.Queued = queued ?? new AgentTurnStopResult(claim.Control, false);

        var grains = DispatchProxy.Create<IGrainFactory, GrainFactoryProxy>();
        ((GrainFactoryProxy)(object)grains).Session = session;

        return new StopContext(
            grains,
            sessionProxy,
            new RecordingDelivery(delivery ?? new SessionStopDeliveryResponse(null, false)));
    }

    private static AgentTurnControlState Control(
        AgentTurnStatus status,
        AgentTurnControlClassification classification) =>
        new("turn-1", status, classification, IsLaunchTurn: false);

    private sealed record StopContext(
        IGrainFactory Grains,
        SessionGrainProxy Session,
        RecordingDelivery Delivery)
    {
        public Task<TurnControlResult> StopAsync() => AgentSessionStopOperations.StopAsync(
            "project-1",
            Grains,
            Delivery,
            new SessionStopTarget(
                "runner-1",
                "session-1",
                "generic",
                null,
                "session-1",
                "opencode",
                "runtime-session-1",
                "/work/session-1"),
            "turn-1",
            CancellationToken.None);
    }

    private sealed class RecordingDelivery(SessionStopDeliveryResponse response) : ISessionStopDelivery
    {
        public SessionStopDeliveryRequest? Request { get; private set; }

        public Task<SessionStopDeliveryResponse> DispatchAsync(
            SessionStopDeliveryRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(response);
        }
    }

    private class SessionGrainProxy : DispatchProxy
    {
        public AgentTurnStopClaimResult Claim { get; set; } = null!;
        public AgentTurnStopResult Queued { get; set; } = null!;
        public (string TurnId, string OperationId)? MarkedDispatched { get; private set; }
        public (string TurnId, string OperationId, AgentSessionStopDisposition Disposition)? AppliedDelivery { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IAgentSessionGrain.ClaimTurnStopAsync))
                return Task.FromResult(Claim);
            if (targetMethod?.Name == nameof(IAgentSessionGrain.StopQueuedTurnAsync))
                return Task.FromResult(Queued);
            if (targetMethod?.Name == nameof(IAgentSessionGrain.MarkTurnStopDispatchedAsync))
            {
                MarkedDispatched = ((string)args![0]!, (string)args[1]!);
                return Task.CompletedTask;
            }
            if (targetMethod?.Name == nameof(IAgentSessionGrain.ApplyStopDeliveryAsync))
            {
                AppliedDelivery = ((string)args![0]!, (string)args[1]!, (AgentSessionStopDisposition)args[2]!);
                return Task.CompletedTask;
            }

            throw new NotSupportedException(targetMethod?.Name);
        }
    }

    private class GrainFactoryProxy : DispatchProxy
    {
        public IAgentSessionGrain Session { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IGrainFactory.GetGrain)
                && targetMethod.IsGenericMethod
                && targetMethod.GetGenericArguments()[0] == typeof(IAgentSessionGrain))
            {
                return Session;
            }

            throw new NotSupportedException(targetMethod?.Name);
        }
    }
}
