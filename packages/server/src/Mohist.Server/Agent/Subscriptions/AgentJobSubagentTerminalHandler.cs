using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Orleans;

namespace Mohist.Server.Agent.Subscriptions;

[Subscription(
    Type = EventCatalog.ReverseDns.AgentJobSubagentTerminal,
    Identity = "Mohist.Server.Agent.Subscriptions.AgentJobSubagentTerminalHandler")]
public sealed class AgentJobSubagentTerminalHandler : ICloudEventHandler
{
    private readonly IGrainFactory _grains;
    private readonly ILogger<AgentJobSubagentTerminalHandler> _log;

    public AgentJobSubagentTerminalHandler(
        IGrainFactory grains,
        ILogger<AgentJobSubagentTerminalHandler> log)
    {
        _grains = grains;
        _log = log;
    }

    public bool Filter(CloudEvent evt) => evt.Data is { ValueKind: JsonValueKind.Object };

    public async Task HandleAsync(CloudEvent evt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var report = evt.Data?.Deserialize<SubagentTerminalEventData>(CloudEvent.JsonOptions)
            ?? throw new InvalidOperationException("Subagent terminal event has no valid payload.");
        report.Validate();

        var child = _grains.GetGrain<IAgentSessionGrain>(report.ChildSessionId);
        var claim = await child.ClaimSubagentTerminalReportAsync(
            new ClaimSubagentTerminalReportCommand(report.EdgeId, report.ChildLaunchJobId));
        if (claim.Disposition is SubagentTerminalReportClaimDisposition.Suppressed
            or SubagentTerminalReportClaimDisposition.Delivered)
            return;

        var key = SubagentTerminalReportIdempotencyKeys.For(
            report.EdgeId,
            report.ChildLaunchJobId);
        var parent = _grains.GetGrain<IAgentSessionGrain>(report.ParentSessionId);
        var accepted = await parent.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: $"child {report.Status}; result={report.ResultReference}",
            Source: "subagent-terminal",
            IdempotencyKey: key,
            Provenance: new AgentSessionInputProvenance(
                ProviderKind: "subagent-terminal",
                WorkspaceId: report.ChildSessionId,
                ConversationId: report.ChildLaunchJobId,
                ThreadId: report.InitialTurnId,
                MemberId: report.EdgeId,
                MessageId: report.ResultReference)));

        var delivered = await child.RecordSubagentTerminalReportDeliveredAsync(
            new RecordSubagentTerminalReportDeliveredCommand(
                report.EdgeId,
                report.ChildLaunchJobId,
                accepted.InputId));
        if (delivered.Disposition == SubagentTerminalReportDeliveryDisposition.InputIdConflict)
        {
            throw new InvalidOperationException(
                $"Subagent terminal report {key} resolved to conflicting parent InputId.");
        }

        _log.LogInformation(
            "Delivered subagent terminal report {JobId} to parent session {ParentSessionId} as input {InputId}",
            report.ChildLaunchJobId,
            report.ParentSessionId,
            accepted.InputId);
    }
}

public sealed record SubagentTerminalEventData(
    string ChildLaunchJobId,
    string ChildSessionId,
    string ParentSessionId,
    string ParentAgentId,
    string EdgeId,
    string InitialTurnId,
    string Status,
    string ResultReference)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ChildLaunchJobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ChildSessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ParentSessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ParentAgentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(EdgeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(InitialTurnId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ResultReference);
        if (Status is not ("completed" or "failed" or "cancelled"))
            throw new InvalidOperationException($"Invalid subagent terminal status '{Status}'.");
    }
}
