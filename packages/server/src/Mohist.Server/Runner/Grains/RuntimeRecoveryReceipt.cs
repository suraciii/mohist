using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mohist.Server.Infrastructure;

namespace Mohist.Server.Runner.Grains;

/// <summary>
/// Runtime-neutral, immutable identity for one Agent execution receipt. The
/// payload is deliberately a discriminated value so an interruption cannot
/// also carry a task verdict.
/// </summary>
[GenerateSerializer]
public sealed record RuntimeRecoveryReceipt(
    [property: Id(0)] string WorkflowRunId,
    [property: Id(1)] string TaskRunId,
    [property: Id(2)] string WorkId,
    [property: Id(3)] string RunnerId,
    [property: Id(4)] string AgentSessionId,
    [property: Id(5)] string AgentTurnId,
    [property: Id(6)] string Runtime,
    [property: Id(7)] string RuntimeSessionId,
    [property: Id(8)] int RecoveryGeneration,
    [property: Id(9)] string ReceiptId,
    [property: Id(10)] RuntimeRecoveryReceiptPayload? Payload,
    /// <summary>
    /// Owner discriminator for receipts that do not belong to a WorkflowRun.
    /// Missing values retain the original workflow receipt contract.
    /// </summary>
    [property: Id(11)] string? OwnerKind = null,
    [property: Id(12)] string? AgentJobId = null)
{
    public IReadOnlyList<string> ValidateContract()
    {
        var errors = new List<string>();
        var ownerKind = string.IsNullOrWhiteSpace(OwnerKind)
            ? RuntimeRecoveryReceiptOwnerKinds.Workflow
            : OwnerKind.Trim().ToLowerInvariant();
        if (ownerKind is not (RuntimeRecoveryReceiptOwnerKinds.Workflow or RuntimeRecoveryReceiptOwnerKinds.AgentJob))
            errors.Add("ownerKind must be 'workflow' or 'agent-job'");
        if (ownerKind == RuntimeRecoveryReceiptOwnerKinds.Workflow)
        {
            Require(WorkflowRunId, nameof(WorkflowRunId));
            Require(TaskRunId, nameof(TaskRunId));
            if (!string.IsNullOrWhiteSpace(AgentJobId))
                errors.Add("workflow receipts cannot carry agentJobId");
        }
        else if (ownerKind == RuntimeRecoveryReceiptOwnerKinds.AgentJob)
        {
            Require(AgentJobId, nameof(AgentJobId));
        }
        Require(WorkId, nameof(WorkId));
        Require(RunnerId, nameof(RunnerId));
        Require(AgentSessionId, nameof(AgentSessionId));
        Require(AgentTurnId, nameof(AgentTurnId));
        Require(Runtime, nameof(Runtime));
        Require(RuntimeSessionId, nameof(RuntimeSessionId));
        Require(ReceiptId, nameof(ReceiptId));
        if (RecoveryGeneration < 0)
            errors.Add("recoveryGeneration must be zero or greater");

        if (Payload is null)
        {
            errors.Add("payload is required");
            return errors;
        }

        var payloadType = Payload.Type?.Trim().ToLowerInvariant();
        switch (payloadType)
        {
            case RuntimeRecoveryReceiptPayloadTypes.TerminalResult:
                if (Payload.Result is not null && Payload.TerminalResult is not null)
                    errors.Add("terminal-result must contain exactly one result");
                if (Payload.NormalizedTerminalResult is null)
                    errors.Add("terminal-result requires a result");
                if (string.IsNullOrWhiteSpace(Payload.Fingerprint))
                    errors.Add("terminal-result requires a fingerprint");
                if (Payload.UpdateOperationId is not null || Payload.StopConfirmed is not null)
                    errors.Add("terminal-result cannot carry interruption fields");
                if (string.IsNullOrWhiteSpace(Payload.NormalizedTerminalResult?.Status))
                    errors.Add("terminal-result requires a result status");
                else if (string.Equals(Payload.NormalizedTerminalResult.Status, "unknown", StringComparison.OrdinalIgnoreCase))
                    errors.Add("terminal-result cannot use the unknown status");
                break;

            case RuntimeRecoveryReceiptPayloadTypes.UpdateInterrupted:
                if (Payload.Result is not null || Payload.TerminalResult is not null)
                    errors.Add("update-interrupted cannot carry a task outcome");
                if (string.IsNullOrWhiteSpace(Payload.UpdateOperationId))
                    errors.Add("update-interrupted requires an update operation id");
                if (Payload.Fingerprint is not null)
                    errors.Add("update-interrupted cannot carry a result fingerprint");
                if (Payload.StopConfirmed != true)
                    errors.Add("update-interrupted requires stopConfirmed=true");
                break;

            default:
                errors.Add("payload.type must be 'terminal-result' or 'update-interrupted'");
                break;
        }

        return errors;

        void Require(string? value, string name)
        {
            if (string.IsNullOrWhiteSpace(value))
                errors.Add($"{name} is required");
        }
    }

    private static object CanonicalPayload(RuntimeRecoveryReceiptPayload payload) => new
    {
        type = payload.Type?.Trim().ToLowerInvariant(),
        result = payload.NormalizedTerminalResult,
        fingerprint = payload.Fingerprint,
        updateOperationId = payload.UpdateOperationId,
        stopConfirmed = payload.StopConfirmed,
    };

    public string RequestFingerprint()
    {
        var canonical = new
        {
            ownerKind = string.IsNullOrWhiteSpace(OwnerKind)
                ? RuntimeRecoveryReceiptOwnerKinds.Workflow
                : OwnerKind.Trim().ToLowerInvariant(),
            agentJobId = AgentJobId,
            workflowRunId = WorkflowRunId,
            taskRunId = TaskRunId,
            workId = WorkId,
            runnerId = RunnerId,
            agentSessionId = AgentSessionId,
            agentTurnId = AgentTurnId,
            runtime = Runtime,
            runtimeSessionId = RuntimeSessionId,
            recoveryGeneration = RecoveryGeneration,
            receiptId = ReceiptId,
            payload = Payload is null ? null : CanonicalPayload(Payload),
        };
        return Sha256(JsonSerializer.SerializeToUtf8Bytes(canonical, JSON.Options));
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

[GenerateSerializer]
public sealed record RuntimeRecoveryReceiptPayload(
    [property: Id(0)] string Type,
    [property: Id(1)] WorkResult? Result = null,
    [property: Id(2)] string? Fingerprint = null,
    [property: Id(3)] string? UpdateOperationId = null,
    [property: Id(4)] bool? StopConfirmed = null,
    // Accept the explicit terminalResult spelling as an additive wire alias.
    // Both aliases are validated as one logical result and both cannot be used.
    [property: Id(5)] WorkResult? TerminalResult = null)
{
    [JsonIgnore]
    public WorkResult? NormalizedTerminalResult => Result ?? TerminalResult;
}

public static class RuntimeRecoveryReceiptOwnerKinds
{
    public const string Workflow = "workflow";
    public const string AgentJob = "agent-job";
}

public static class RuntimeRecoveryReceiptPayloadTypes
{
    public const string TerminalResult = "terminal-result";
    public const string UpdateInterrupted = "update-interrupted";
}

public static class RuntimeRecoveryReceiptFingerprint
{
    public static string For(WorkResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return Convert.ToHexString(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(result, JSON.Options))).ToLowerInvariant();
    }
}

[GenerateSerializer]
public sealed record RuntimeRecoveryReceiptAcknowledgement(
    [property: Id(0)] string AppliedReceiptId,
    [property: Id(1)] string Status,
    [property: Id(2)] string? Reason = null)
{
    [JsonIgnore]
    public bool IsTerminal => !string.Equals(Status, RuntimeRecoveryReceiptAckStatuses.Retryable, StringComparison.Ordinal);
}

public static class RuntimeRecoveryReceiptAckStatuses
{
    public const string Accepted = "accepted";
    public const string Stale = "stale";
    public const string RejectedMismatch = "rejected-mismatch";
    public const string Retryable = "retryable";
}

[GenerateSerializer]
public sealed record AppliedRuntimeRecoveryReceipt(
    [property: Id(0)] string ReceiptId,
    [property: Id(1)] string RequestFingerprint,
    [property: Id(2)] string Status,
    [property: Id(3)] string? Reason = null);
