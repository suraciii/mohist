using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Xunit;

namespace Mohist.Server.Tests.Sessions;

public sealed partial class AgentSessionFollowupGrainSpecs
{
    [Fact]
    public async Task AcceptFollowup_ExpectedIdentityMismatch_PrecedesSameKeyReplayWithoutMutation()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-identity-fence");
        var command = new AcceptFollowupCommand(
            Text: "fenced workflow input",
            Source: "workflow",
            IdempotencyKey: "workflow-identity-key",
            PreMintedInputId: "workflow-input",
            PreMintedTurnId: "workflow-turn",
            AllowPendingInitialLaunch: true,
            ForceNewTurn: true,
            ExpectedProjectId: "project-1",
            ExpectedAgentId: "workflow-agent");
        await grain.AcceptFollowupAsync(command);

        var before = await _fixture.StateStore.LoadAsync(sessionId);
        Assert.NotNull(before);
        var eventCount = _fixture.StateStore.Events.Count;
        var inputCount = before!.Status.Inputs!.Count;
        var turnCount = before.Status.Turns!.Count;
        var leaseCount = before.Status.PendingFollowups!.Count;

        var foreignAgent = await Assert.ThrowsAsync<AgentSessionIdentityMismatchException>(() =>
            grain.AcceptFollowupAsync(command with { ExpectedAgentId = "foreign-agent" }));
        var foreignProject = await Assert.ThrowsAsync<AgentSessionIdentityMismatchException>(() =>
            grain.AcceptFollowupAsync(command with { ExpectedProjectId = "foreign-project" }));

        Assert.Equal("workflow-agent", foreignAgent.ActualAgentId);
        Assert.Equal("project-1", foreignProject.ActualProjectId);
        var after = await _fixture.StateStore.LoadAsync(sessionId);
        Assert.NotNull(after);
        Assert.Equal(inputCount, after!.Status.Inputs!.Count);
        Assert.Equal(turnCount, after.Status.Turns!.Count);
        Assert.Equal(leaseCount, after.Status.PendingFollowups!.Count);
        Assert.Equal(eventCount, _fixture.StateStore.Events.Count);
    }

    [Fact]
    public async Task AcceptFollowup_MalformedExpectedIdentity_DoesNotBypassFenceOrClearRecovery()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-identity-recovery-fence");
        var compact = await grain.PrepareSessionCommandAsync(
            SessionCommandKind.Compact,
            "identity-test-generation",
            "identity-compact");
        await grain.AdmitSessionCommandEffectAsync(compact.OperationId, "identity-test-generation");
        await grain.CompleteCompactAsync(new CompleteCompactAgentSessionCommand(
            compact.OperationId,
            "identity-test-generation",
            Summary: "summary"));

        var before = await _fixture.StateStore.LoadAsync(sessionId);
        Assert.NotNull(before?.Status.PendingReset?.Outcome);
        var eventCount = _fixture.StateStore.Events.Count;

        await Assert.ThrowsAsync<ArgumentException>(() => grain.AcceptFollowupAsync(
            new AcceptFollowupCommand(
                Text: "must stay fenced",
                Source: "workflow",
                IdempotencyKey: "malformed-identity",
                ExpectedProjectId: "project-1",
                ExpectedAgentId: " ")));

        var after = await _fixture.StateStore.LoadAsync(sessionId);
        Assert.NotNull(after?.Status.PendingReset?.Outcome);
        Assert.Equal(compact.OperationId, after!.Status.PendingReset!.OperationId);
        Assert.Empty(after.Status.Inputs ?? []);
        Assert.Empty(after.Status.Turns ?? []);
        Assert.Empty(after.Status.PendingFollowups ?? []);
        Assert.Equal(eventCount, _fixture.StateStore.Events.Count);
    }
}
