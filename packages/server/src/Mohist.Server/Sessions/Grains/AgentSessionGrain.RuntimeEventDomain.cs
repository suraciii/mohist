using System.Text.Json;
using Mohist.Server.Infrastructure;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Sessions.Grains;

// Runtime-event domain application extracted from AgentSessionGrain to
// keep the main partial within the file-size ratchet.
public sealed partial class AgentSessionGrain
{
    private static IReadOnlyList<AgentSessionEvent> ApplyRuntimeEventToDomain(
        AgentSession session,
        AgentSessionRuntimeEventInput runtimeEvent,
        DateTime now,
        bool sessionLevelActivityOnly = false)
    {
        var payload = JSON.DeserializeElement(runtimeEvent.PayloadJson);
        var operationId = AgentSessionJsonHelper.GetStringProp(payload, "operationId");
        if (sessionLevelActivityOnly && runtimeEvent.Type == RuntimeEventTypes.SessionActivity)
            return session.SetActivity(ParseActivity(payload), now);

        return runtimeEvent.Type switch
        {
            RuntimeEventTypes.SessionInput when HasPendingFollowupOperation(session, operationId) => session.SetActivity(
                session.Status.Activity == AgentSessionActivity.Unknown
                    ? AgentSessionActivity.Unknown
                    : AgentSessionActivity.Active,
                now),
            RuntimeEventTypes.SessionActivity when HasPendingFollowupOperation(session, operationId) => session.SetActivity(
                ParseActivity(payload),
                now),
            RuntimeEventTypes.SessionInput => DriveNonLaunchTurnLifecycle(session, runtimeEvent, now),
            RuntimeEventTypes.SessionActivity => DriveTerminalActivityLifecycle(session, runtimeEvent, payload, now),
            RuntimeEventTypes.SessionLiveness => session.RecordActivity(now),
            RuntimeEventTypes.UsageUpdated => session.ApplyUsage(
                AgentSessionJsonHelper.GetLongProp(payload, "inputTokens"),
                AgentSessionJsonHelper.GetLongProp(payload, "outputTokens"),
                AgentSessionJsonHelper.GetLongProp(payload, "totalTokens"),
                AgentSessionJsonHelper.GetLongProp(payload, "cachedReadTokens"),
                AgentSessionJsonHelper.GetLongProp(payload, "thoughtTokens"),
                AgentSessionJsonHelper.GetCostAmount(payload),
                AgentSessionJsonHelper.GetCostCurrency(payload),
                AgentSessionJsonHelper.GetContextWindowUsed(payload),
                AgentSessionJsonHelper.GetContextWindowSize(payload),
                now,
                AgentSessionJsonHelper.GetLongProp(payload, "cachedWriteTokens")),
            RuntimeEventTypes.ModelResolved => session.ResolveModel(
                AgentSessionJsonHelper.GetStringProp(payload, "resolvedModel"),
                now),
            _ => []
        };
    }

    private static JsonElement SafeDeserialize(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
            return default;
        try
        {
            return JSON.DeserializeElement(payloadJson);
        }
        catch
        {
            return default;
        }
    }

    private static AgentTurnStatus ResolveFollowupTurnTerminalStatus(JsonElement payload)
    {
        var status = AgentSessionJsonHelper.GetStringProp(payload, "status")?.ToLowerInvariant();
        var failureCategory = AgentSessionJsonHelper.GetStringProp(payload, "failureCategory");
        return status switch
        {
            "failed" => AgentTurnStatus.Failed,
            "cancelled" => AgentTurnStatus.Cancelled,
            "unknown" => AgentTurnStatus.Unknown,
            _ => string.Equals(failureCategory, "unknown", StringComparison.OrdinalIgnoreCase)
                ? AgentTurnStatus.Unknown
                : AgentTurnStatus.Completed,
        };
    }

    private static AgentTurnResult? ResolveFollowupTurnResult(JsonElement payload)
    {
        var message = AgentSessionJsonHelper.GetStringProp(payload, "message");
        var output = AgentSessionJsonHelper.GetStringProp(payload, "output");
        var failureReason = AgentSessionJsonHelper.GetStringProp(payload, "failureReason");
        var failureCategory = AgentSessionJsonHelper.GetStringProp(payload, "failureCategory");
        return message is null && output is null && failureReason is null && failureCategory is null
            ? null
            : new AgentTurnResult(message, output, failureReason, failureCategory);
    }

    private static void SettleStopClaimFromRuntimeEvent(
        AgentSession session,
        AgentSessionRuntimeEventInput runtimeEvent)
    {
        if (runtimeEvent.Type != RuntimeEventTypes.SessionActivity)
            return;

        var payload = JSON.DeserializeElement(runtimeEvent.PayloadJson);
        var terminal = MapTerminalActivityToTurnStatus(
            ParseActivity(payload),
            AgentSessionJsonHelper.GetStringProp(payload, "status"),
            !string.IsNullOrWhiteSpace(AgentSessionJsonHelper.GetStringProp(payload, "stopOperationId")));
        if (terminal is null
            || !TryResolveTurnId(runtimeEvent.PayloadJson, out var turnId))
            return;

        var pending = session.Status.PendingStop;
        if (pending is { IsActive: true }
            && string.Equals(pending.TurnId, turnId, StringComparison.Ordinal))
        {
            session.CompleteTurnStop(turnId, pending.OperationId);
        }
    }

    private static IReadOnlyList<AgentSessionEvent> DriveNonLaunchTurnLifecycle(
        AgentSession session,
        AgentSessionRuntimeEventInput runtimeEvent,
        DateTime now)
    {
        if (TryResolveTurnId(runtimeEvent.PayloadJson, out var payloadTurnId))
        {
            var turn = session.Status.Turns is { Count: > 0 } turns
                ? turns.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, payloadTurnId, StringComparison.Ordinal))
                : null;
            var current = FindCurrentNonLaunchTurn(session);
            if (turn is null
                || !string.IsNullOrWhiteSpace(turn.JobId)
                || current is null
                || !string.Equals(current.Id, turn.Id, StringComparison.Ordinal))
            {
                return [];
            }
            return session.MarkTurnExecuting(turn.Id, now);
        }
        var events = new List<AgentSessionEvent>(session.SetActivity(
            session.Status.Activity == AgentSessionActivity.Unknown
                ? AgentSessionActivity.Unknown
                : AgentSessionActivity.Active,
            now));
        events.AddRange(MarkCurrentNonLaunchTurnExecuting(session, now));
        return events;
    }

    private static IReadOnlyList<AgentSessionEvent> DriveTerminalActivityLifecycle(
        AgentSession session,
        AgentSessionRuntimeEventInput runtimeEvent,
        JsonElement payload,
        DateTime now)
    {
        var status = AgentSessionJsonHelper.GetStringProp(payload, "status");
        var activity = ParseActivity(payload);
        if (activity == AgentSessionActivity.Active)
            return session.SetActivity(activity, now);
        var terminal = MapTerminalActivityToTurnStatus(
            activity,
            status,
            !string.IsNullOrWhiteSpace(AgentSessionJsonHelper.GetStringProp(payload, "stopOperationId")));
        if (terminal is null)
            return session.SetActivity(activity, now);
        if (TryResolveTurnId(runtimeEvent.PayloadJson, out var payloadTurnId))
        {
            var turn = session.Status.Turns is { Count: > 0 } turns
                ? turns.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, payloadTurnId, StringComparison.Ordinal))
                : null;
            if (turn is null
                || !string.IsNullOrWhiteSpace(turn.JobId)
                || !IsCurrentNonLaunchTurn(session, turn))
            {
                return [];
            }
            return session.MarkTurnTerminal(turn.Id, terminal.Value, null, now);
        }
        if (!string.IsNullOrWhiteSpace(AgentSessionJsonHelper.GetStringProp(payload, "agentJobId")))
            return session.SetActivity(activity, now);
        var events = new List<AgentSessionEvent>(session.SetActivity(activity, now));
        events.AddRange(MarkCurrentNonLaunchTurnTerminal(session, terminal.Value, now));
        return events;
    }

    private static AgentTurnStatus? MapTerminalActivityToTurnStatus(
        AgentSessionActivity activity,
        string? status,
        bool stopConfirmed)
    {
        if (activity == AgentSessionActivity.Active)
            return null;
        if (activity == AgentSessionActivity.Unknown)
            return AgentTurnStatus.Unknown;
        if (stopConfirmed)
            return AgentTurnStatus.Cancelled;
        if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
            return AgentTurnStatus.Failed;
        return AgentTurnStatus.Completed;
    }

    private static IReadOnlyList<AgentSessionEvent> MarkCurrentNonLaunchTurnExecuting(
        AgentSession session,
        DateTime now)
    {
        var current = FindCurrentNonLaunchTurn(session);
        return current is null ? [] : session.MarkTurnExecuting(current.Id, now);
    }

    private static IReadOnlyList<AgentSessionEvent> MarkCurrentNonLaunchTurnTerminal(
        AgentSession session,
        AgentTurnStatus terminal,
        DateTime now)
    {
        var current = FindCurrentNonLaunchTurn(session);
        return current?.Status == AgentTurnStatus.Executing
            ? session.MarkTurnTerminal(current.Id, terminal, null, now)
            : [];
    }

    private static AgentTurnRecord? FindCurrentNonLaunchTurn(AgentSession session)
    {
        var turns = session.Status.Turns ?? [];
        for (var index = turns.Count - 1; index >= 0; index--)
        {
            var turn = turns[index];
            if (!string.IsNullOrWhiteSpace(turn.JobId))
                continue;
            if (turn.Status is AgentTurnStatus.Completed
                or AgentTurnStatus.Failed
                or AgentTurnStatus.Cancelled
                or AgentTurnStatus.Unknown)
                continue;
            return turn;
        }
        return null;
    }

    private static bool IsCurrentNonLaunchTurn(AgentSession session, AgentTurnRecord turn)
    {
        if (turn.Status is AgentTurnStatus.Completed
            or AgentTurnStatus.Failed
            or AgentTurnStatus.Cancelled)
        {
            return false;
        }

        var turns = session.Status.Turns ?? [];
        var latestNonLaunch = turns.LastOrDefault(candidate => string.IsNullOrWhiteSpace(candidate.JobId));
        return latestNonLaunch is not null
            && string.Equals(latestNonLaunch.Id, turn.Id, StringComparison.Ordinal);
    }

    private static bool TryResolveTurnId(string payloadJson, out string turnId)
    {
        try
        {
            var payload = JSON.DeserializeElement(payloadJson);
            var id = AgentSessionJsonHelper.GetStringProp(payload, "turnId");
            if (!string.IsNullOrWhiteSpace(id))
            {
                turnId = id;
                return true;
            }
        }
        catch
        {
        }
        turnId = string.Empty;
        return false;
    }

    private static AgentSessionActivity ParseActivity(JsonElement payload) =>
        AgentSessionJsonHelper.GetStringProp(payload, "activity")?.ToLowerInvariant() switch
        {
            "active" => AgentSessionActivity.Active,
            "unknown" => AgentSessionActivity.Unknown,
            _ => AgentSessionActivity.Idle,
        };

    private static bool HasPendingFollowupOperation(AgentSession session, string? operationId) =>
        !string.IsNullOrWhiteSpace(operationId)
        && GetPendingFollowups(session).Any(lease =>
            string.Equals(lease.OperationId, operationId, StringComparison.Ordinal));}
