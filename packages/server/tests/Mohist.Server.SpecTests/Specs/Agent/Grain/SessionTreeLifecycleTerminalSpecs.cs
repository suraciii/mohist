using Mohist.Server.Agent.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Grain;

[Collection("AgentJobGrain")]
public sealed class SessionTreeLifecycleTerminalSpecs
{
    private readonly AgentJobGrainFixture _fixture;

    public SessionTreeLifecycleTerminalSpecs(AgentJobGrainFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task TerminalReportClaimAndDeliveryAreIdempotentAndKeepTheFirstInputId()
    {
        Assert.Equal(
            "subagent-terminal:edge-terminal-report:job-terminal-report",
            SubagentTerminalReportIdempotencyKeys.For("edge-terminal-report", "job-terminal-report"));
        var child = await OpenChildAsync("terminal-report");
        var claim = await child.ClaimSubagentTerminalReportAsync(new("edge-terminal-report", "job-terminal-report"));
        Assert.Equal(SubagentTerminalReportClaimDisposition.ClaimedPending, claim.Disposition);

        var delivered = await child.RecordSubagentTerminalReportDeliveredAsync(
            new("edge-terminal-report", "job-terminal-report", "parent-input-1"));
        Assert.Equal(SubagentTerminalReportDeliveryDisposition.Delivered, delivered.Disposition);

        var replay = await child.ClaimSubagentTerminalReportAsync(new("edge-terminal-report", "job-terminal-report"));
        Assert.Equal(SubagentTerminalReportClaimDisposition.Delivered, replay.Disposition);
        Assert.Equal("parent-input-1", replay.DeliveredInputId);

        var deliveredReplay = await child.RecordSubagentTerminalReportDeliveredAsync(
            new("edge-terminal-report", "job-terminal-report", "parent-input-1"));
        Assert.Equal(SubagentTerminalReportDeliveryDisposition.AlreadyDelivered, deliveredReplay.Disposition);
        var conflict = await child.RecordSubagentTerminalReportDeliveredAsync(
            new("edge-terminal-report", "job-terminal-report", "parent-input-2"));
        Assert.Equal(SubagentTerminalReportDeliveryDisposition.InputIdConflict, conflict.Disposition);
        Assert.Equal("parent-input-1", conflict.DeliveredInputId);
    }

    [Fact]
    public async Task DetachAndTerminalClaimLinearizeInEitherOrder()
    {
        var detachedFirst = await OpenChildAsync("detach-first");
        var detached = await detachedFirst.ApplyParentLinkDetachAsync(
            DetachCommand(
                "detach-command-1",
                "edge-detach-first",
                "session-child-detach-first",
                "parent",
                "job-detach-first",
                11));
        Assert.Equal(SessionTreeDetachMutationState.Detached, detached.State);

        var suppressed = await detachedFirst.ClaimSubagentTerminalReportAsync(
            new("edge-detach-first", "job-detach-first"));
        Assert.Equal(SubagentTerminalReportClaimDisposition.Suppressed, suppressed.Disposition);
        var detachedReplay = await detachedFirst.ApplyParentLinkDetachAsync(
            DetachCommand(
                "detach-command-1",
                "edge-detach-first",
                "session-child-detach-first",
                "parent",
                "job-detach-first",
                11));
        Assert.Equal(detached, detachedReplay);

        var claimFirst = await OpenChildAsync("claim-first");
        var pending = await claimFirst.ClaimSubagentTerminalReportAsync(
            new("edge-claim-first", "job-claim-first"));
        Assert.Equal(SubagentTerminalReportClaimDisposition.ClaimedPending, pending.Disposition);
        var pendingDetach = await claimFirst.ApplyParentLinkDetachAsync(
            DetachCommand(
                "detach-command-2",
                "edge-claim-first",
                "session-child-claim-first",
                "parent",
                "job-claim-first",
                12));
        Assert.Equal(SessionTreeDetachMutationState.Detached, pendingDetach.State);
        Assert.Equal(TerminalReportState.Pending, pendingDetach.Link!.TerminalReport);

        var delivered = await claimFirst.RecordSubagentTerminalReportDeliveredAsync(
            new("edge-claim-first", "job-claim-first", "parent-input-claim-first"));
        Assert.Equal(SubagentTerminalReportDeliveryDisposition.Delivered, delivered.Disposition);
        var deliveredDetach = await claimFirst.ApplyParentLinkDetachAsync(
            DetachCommand(
                "detach-command-2",
                "edge-claim-first",
                "session-child-claim-first",
                "parent",
                "job-claim-first",
                12));
        Assert.Equal(TerminalReportState.Delivered, deliveredDetach.Link!.TerminalReport);

        var reparent = await claimFirst.ApplyParentLinkDetachAsync(
            DetachCommand(
                "detach-command-3",
                "edge-claim-first",
                "session-child-claim-first",
                "other-parent",
                "job-claim-first",
                13));
        Assert.Equal(SessionTreeDetachMutationState.Rejected, reparent.State);
        Assert.Equal("parent_link_identity_mismatch", reparent.RejectionReason);
    }

    private async Task<IAgentSessionGrain> OpenChildAsync(string suffix)
    {
        var childId = $"session-child-{suffix}";
        var child = await OpenSessionAsync("project-lifecycle", childId, "agent-lifecycle");
        var attached = await child.ApplyParentLinkAttachAsync(new ApplyParentLinkAttachCommand(
            $"attach-{suffix}",
            $"edge-{suffix}",
            "parent",
            "parent-agent",
            $"job-{suffix}",
            1,
            "/workspace",
            "runner-1",
            "opencode",
            "runtime-session",
            "project-lifecycle",
            1,
            "standalone-receipt",
            SessionTreeExpectedLinkState.Absent));
        Assert.Equal(SessionTreeAttachMutationState.Attached, attached.State);
        return child;
    }

    private async Task<IAgentSessionGrain> OpenSessionAsync(
        string projectId,
        string sessionId,
        string agentId)
    {
        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.OpenAsync(new OpenAgentSessionCommand(
            "runner-1",
            "opencode",
            "/workspace",
            Metadata: new AgentSessionMetadata(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
                [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
                [GenericAgentSessionMetadata.AgentId] = agentId,
                [GenericAgentSessionMetadata.AgentName] = agentId,
            })));
        await session.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(
            "runtime-session",
            ExpectedRunnerId: "runner-1",
            ExpectedRuntime: "opencode"));
        return session;
    }

    private static ApplyParentLinkDetachCommand DetachCommand(
        string commandId,
        string edgeId,
        string childSessionId,
        string parentSessionId,
        string jobId,
        long revision) =>
        new(edgeId, parentSessionId, jobId, revision, commandId, childSessionId, 1);
}
