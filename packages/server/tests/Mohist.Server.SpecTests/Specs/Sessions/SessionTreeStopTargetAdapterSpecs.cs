using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Api;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

[Collection("IntegrationSessions")]
public sealed class SessionTreeStopTargetAdapterSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public SessionTreeStopTargetAdapterSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task TargetAdapterUsesExistingTurnControlForQueuedExecutingIdleUnknownAndReplaced()
    {
        var projectId = $"adapter-matrix-{Guid.NewGuid():N}";
        await using var scope = _fixture.Services.CreateAsyncScope();
        var adapter = scope.ServiceProvider.GetRequiredService<ISessionTreeStopTargetAdapter>();
        var hub = scope.ServiceProvider.GetRequiredService<IHubContext<RunnerHub>>() as RecordingRunnerHubContext
            ?? throw new InvalidOperationException("Recording runner hub context is not configured.");

        var queued = await OpenSessionAsync(projectId, "adapter-queued");
        const string queuedTurn = "adapter-queued-turn";
        await queued.RecordFollowupTurnAsync(new RecordFollowupTurnCommand(
            "adapter-queued-input", queuedTurn, "queued", "test"));
        var queuedInvocationCount = hub.Invocations.Count;
        var queuedResult = await adapter.StopAsync(projectId, Target(
            "adapter-queued", queuedTurn, AgentTurnStatus.Queued, "adapter-queued-op"));
        Assert.Equal(SessionTreeStopTargetOutcome.Cancelled, queuedResult.Outcome);
        Assert.Equal(queuedInvocationCount, hub.Invocations.Count);

        var executing = await OpenSessionAsync(projectId, "adapter-executing");
        const string executingTurn = "adapter-executing-turn";
        await executing.RecordFollowupTurnAsync(new RecordFollowupTurnCommand(
            "adapter-executing-input", executingTurn, "executing", "test"));
        await executing.MarkTurnExecutingAsync(executingTurn);
        var runnerId = "adapter-runner";
        _fixture.Services.GetRequiredService<RunnerConnectionTracker>().Register(runnerId, "adapter-executing-connection");
        hub.SetInvocationResponse("CancelAgentSession", new RunnerStopReply("stopped"));
        var executingInvocationCount = hub.Invocations.Count;
        var executingResult = await adapter.StopAsync(projectId, Target(
            "adapter-executing", executingTurn, AgentTurnStatus.Executing, "adapter-executing-op", runnerId));
        Assert.Equal(SessionTreeStopTargetOutcome.Cancelled, executingResult.Outcome);
        Assert.Contains(hub.Invocations.Skip(executingInvocationCount), item => item.Method == "CancelAgentSession");

        var idleResult = await adapter.StopAsync(projectId, Target(
            "adapter-idle", null, null, "adapter-idle-op"));
        Assert.Equal(SessionTreeStopTargetOutcome.AlreadyIdle, idleResult.Outcome);

        var unknownResult = await adapter.StopAsync(projectId, Target(
            "adapter-unknown", "adapter-unknown-turn", AgentTurnStatus.Unknown, "adapter-unknown-op"));
        Assert.Equal(SessionTreeStopTargetOutcome.Unknown, unknownResult.Outcome);

        var replaced = await OpenSessionAsync(projectId, "adapter-replaced");
        const string replacedTurn = "adapter-replaced-turn";
        await replaced.RecordFollowupTurnAsync(new RecordFollowupTurnCommand(
            "adapter-replaced-input", replacedTurn, "replaced", "test"));
        var replacedResult = await adapter.StopAsync(projectId, Target(
            "adapter-replaced", replacedTurn, AgentTurnStatus.Queued, "adapter-replaced-op", "wrong-runner"));
        Assert.Equal(SessionTreeStopTargetOutcome.Rejected, replacedResult.Outcome);
    }

    private async Task<IAgentSessionGrain> OpenSessionAsync(string projectId, string sessionId)
    {
        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.OpenAsync(new OpenAgentSessionCommand(
            "adapter-runner",
            "opencode",
            "/workspace",
            Metadata: new AgentSessionMetadata(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
                [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
                [GenericAgentSessionMetadata.AgentId] = "adapter-agent",
            })));
        await session.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(
            "adapter-runtime",
            ExpectedRunnerId: "adapter-runner",
            ExpectedRuntime: "opencode"));
        return session;
    }

    private static SessionTreeStopTargetSnapshot Target(
        string sessionId,
        string? turnId,
        AgentTurnStatus? status,
        string operationId,
        string runnerId = "adapter-runner") => new(
        sessionId,
        turnId,
        turnId is null ? null : $"job-{turnId}",
        status,
        runnerId,
        "opencode",
        "adapter-runtime",
        "/workspace",
        operationId);
}
