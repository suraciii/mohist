using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Api;

public static partial class RunnerRoutes
{
    private static void MapRunnerUpdateRecoveryRoutes(RouteGroupBuilder group)
    {
        group.MapPost("/update-interrupt", async (
            string runnerId,
            IGrainFactory grains,
            TimeProvider timeProvider) =>
        {
            var runner = grains.GetGrain<IRunnerGrain>(runnerId);
            var operationGrain = grains.GetGrain<IRunnerUpdateOperationGrain>(runnerId);
            var operation = await operationGrain.GetPendingAsync();
            RunnerRuntimeState? runtime = null;

            if (operation is null)
            {
                runtime = await runner.BeginUpdateInterruptAsync();
                if (runtime is null)
                    return ApiResults.NotFound($"Runner '{runnerId}' not found");

                operation = await operationGrain.StartOrGetAsync(new RunnerUpdateOperation(
                    OperationId: $"runner-update:{Guid.NewGuid():N}",
                    RunnerId: runnerId,
                    CreatedAt: timeProvider.GetUtcNow(),
                    AffectedWorks: BuildUpdateOperationWorks(runtime.ActiveWorks)));
            }
            else
            {
                // A retry after a server or owner crash must repair the
                // existing operation even if the old Runner is already gone.
                await runner.BeginDrainAsync();
            }

            foreach (var work in operation.AffectedWorks
                .Where(work => work.Status == RunnerUpdateWorkStatus.Pending)
                .OrderBy(work => work.OwnerKind, StringComparer.Ordinal)
                .ThenBy(work => work.OwnerId, StringComparer.Ordinal)
                .ThenBy(work => work.WorkId, StringComparer.Ordinal))
            {
                var outcome = await MarkUpdateWorkAsync(grains, operation, work);
                operation = await operationGrain.MarkWorkAsync(
                    operation.OperationId,
                    work.OwnerKind,
                    work.OwnerId,
                    work.WorkId,
                    work.TaskRunId,
                    outcome);
            }

            if (operation.AffectedWorks.Any(work => work.Status == RunnerUpdateWorkStatus.Pending))
                throw new InvalidOperationException($"Update operation '{operation.OperationId}' has uncommitted work markings.");

            var interruptedWorkIds = operation.AffectedWorks
                .Select(work => work.WorkId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            return ApiResults.Ok(new RunnerUpdateInterruptResponse(
                runnerId,
                "interrupted",
                interruptedWorkIds,
                interruptedWorkIds.Length,
                operation.OperationId,
                operation.CreatedAt,
                operation.AffectedWorks));
        });

        group.MapGet("/update-operation/pending", async (string runnerId, IGrainFactory grains) =>
        {
            var operation = await grains
                .GetGrain<IRunnerUpdateOperationGrain>(runnerId)
                .GetPendingAsync();
            return ApiResults.Ok(new RunnerPendingUpdateOperationResponse(operation));
        });

        group.MapGet("/update-operation/{operationId}/recovery-status", async (
            string runnerId,
            string operationId,
            IGrainFactory grains) =>
        {
            var operation = await grains
                .GetGrain<IRunnerUpdateOperationGrain>(runnerId)
                .GetAsync(operationId);
            if (operation is null)
                return ApiResults.NotFound($"Update operation '{operationId}' was not found");

            return ApiResults.Ok(RunnerUpdateRecoveryStatusResponse.From(operation));
        });

        group.MapPost("/recovery-receipt", async (
            string runnerId,
            HttpRequest request,
            IGrainFactory grains,
            CancellationToken ct) =>
        {
            RuntimeRecoveryReceipt? receipt;
            try
            {
                receipt = await request.ReadFromJsonAsync<RuntimeRecoveryReceipt>(JSON.Options, ct);
            }
            catch (JsonException)
            {
                return ApiResults.BadRequest("Invalid recovery receipt body", "invalid_recovery_receipt");
            }

            if (receipt is null)
                return ApiResults.BadRequest("request body is required", "invalid_recovery_receipt");

            var contractErrors = receipt.ValidateContract();
            if (contractErrors.Count > 0)
            {
                return ApiResults.BadRequest(
                    "Recovery receipt contract is invalid",
                    "invalid_recovery_receipt",
                    new { errors = contractErrors });
            }

            if (!string.Equals(receipt.RunnerId, runnerId, StringComparison.Ordinal))
            {
                return Results.Ok(new RuntimeRecoveryReceiptAcknowledgement(
                    receipt.ReceiptId,
                    RuntimeRecoveryReceiptAckStatuses.RejectedMismatch,
                    "runner-identity-mismatch"));
            }

            var ownerKind = string.IsNullOrWhiteSpace(receipt.OwnerKind)
                ? RuntimeRecoveryReceiptOwnerKinds.Workflow
                : receipt.OwnerKind.Trim().ToLowerInvariant();
            RuntimeRecoveryReceiptAcknowledgement acknowledgement;
            if (ownerKind == RuntimeRecoveryReceiptOwnerKinds.AgentJob)
            {
                if (string.IsNullOrWhiteSpace(receipt.AgentJobId))
                {
                    return Results.Ok(new RuntimeRecoveryReceiptAcknowledgement(
                        receipt.ReceiptId,
                        RuntimeRecoveryReceiptAckStatuses.RejectedMismatch,
                        "agent-job-identity-missing"));
                }

                acknowledgement = await grains
                    .GetGrain<IAgentJobGrain>(receipt.AgentJobId)
                    .ReceiveRecoveryReceiptAsync(receipt);
            }
            else
            {
                acknowledgement = await grains
                    .GetGrain<IWorkflowGrain>(receipt.WorkflowRunId)
                    .ReceiveRecoveryReceiptAsync(receipt);
            }

            if (string.Equals(acknowledgement.Status, RuntimeRecoveryReceiptAckStatuses.Retryable, StringComparison.Ordinal))
            {
                return Results.Json(
                    acknowledgement,
                    JSON.Options,
                    statusCode: StatusCodes.Status409Conflict);
            }

            return Results.Ok(acknowledgement);
        });
    }

    private static IReadOnlyList<RunnerUpdateWork> BuildUpdateOperationWorks(
        IReadOnlyList<RunnerActiveWorkItem> activeWorks) =>
        activeWorks
            .Where(work =>
                work.OwnerKind == WorkDispatchOwnerKinds.AgentJob
                || (work.OwnerKind == WorkDispatchOwnerKinds.Workflow
                    && work.IsAgentWork
                    && string.Equals(work.WorkType, "task", StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(work.TaskRunId)))
            .Select(work => new RunnerUpdateWork(
                work.OwnerKind,
                work.OwnerId,
                work.WorkId,
                work.TaskRunId,
                work.WorkType))
            .GroupBy(work => work.Key, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();

    private static async Task<RunnerUpdateWorkStatus> MarkUpdateWorkAsync(
        IGrainFactory grains,
        RunnerUpdateOperation operation,
        RunnerUpdateWork work)
    {
        if (work.OwnerKind == WorkDispatchOwnerKinds.Workflow
            && !string.IsNullOrWhiteSpace(work.TaskRunId))
        {
            var ack = await grains.GetGrain<IWorkflowGrain>(work.OwnerId)
                .MarkUpdateInterruptedAsync(
                    work.TaskRunId,
                    work.WorkId,
                    operation.RunnerId,
                    operation.OperationId);
            return ack == ReportAck.Accepted
                ? RunnerUpdateWorkStatus.Marked
                : RunnerUpdateWorkStatus.AlreadyEnded;
        }

        if (work.OwnerKind == WorkDispatchOwnerKinds.AgentJob)
        {
            var marked = await grains.GetGrain<IAgentJobGrain>(work.OwnerId)
                .MarkUpdateInterruptedAsync(
                    operation.RunnerId,
                    work.WorkId,
                    operation.OperationId);
            return marked
                ? RunnerUpdateWorkStatus.Marked
                : RunnerUpdateWorkStatus.AlreadyEnded;
        }

        throw new InvalidOperationException(
            $"Update operation '{operation.OperationId}' contains unsupported owner kind '{work.OwnerKind}'.");
    }
}

public record RunnerUpdateInterruptResponse(
    string RunnerId,
    string Status,
    IReadOnlyList<string> InterruptedWorkIds,
    int InterruptedWorkCount,
    string? OperationId = null,
    DateTimeOffset? CreatedAt = null,
    IReadOnlyList<RunnerUpdateWork>? AffectedWorks = null);

public record RunnerPendingUpdateOperationResponse(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] RunnerUpdateOperation? Operation);

public record RunnerUpdateRecoveryStatusResponse(
    string OperationId,
    string RunnerId,
    string OperationStatus,
    bool Complete,
    IReadOnlyList<RunnerUpdateRecoveryWorkStatus> AffectedWorks)
{
    public static RunnerUpdateRecoveryStatusResponse From(RunnerUpdateOperation operation) =>
        new(
            operation.OperationId,
            operation.RunnerId,
            operation.Status.ToString().ToLowerInvariant(),
            operation.AffectedWorks.All(work =>
                work.RecoveryStatus != RunnerUpdateRecoveryStatus.Pending
                || work.Status is (RunnerUpdateWorkStatus.AlreadyEnded or RunnerUpdateWorkStatus.Settled)),
            operation.AffectedWorks.Select(RunnerUpdateRecoveryWorkStatus.From).ToArray());
}

public record RunnerUpdateRecoveryWorkStatus(
    string OwnerKind,
    string OwnerId,
    string WorkId,
    string? TaskRunId,
    string WorkType,
    string Status,
    bool Acknowledged)
{
    public static RunnerUpdateRecoveryWorkStatus From(RunnerUpdateWork work)
    {
        var recoveryStatus = work.RecoveryStatus == RunnerUpdateRecoveryStatus.Pending
            && work.Status is (RunnerUpdateWorkStatus.AlreadyEnded or RunnerUpdateWorkStatus.Settled)
            ? RunnerUpdateRecoveryStatus.ReceiptAcked
            : work.RecoveryStatus;
        var acknowledged = recoveryStatus != RunnerUpdateRecoveryStatus.Pending;
        var status = recoveryStatus switch
        {
            RunnerUpdateRecoveryStatus.ReceiptAcked => "receipt-acked",
            RunnerUpdateRecoveryStatus.ReplacementSettled => "replacement-settled",
            _ => "unresolved",
        };
        return new(
            work.OwnerKind,
            work.OwnerId,
            work.WorkId,
            work.TaskRunId,
            work.WorkType,
            status,
            acknowledged);
    }
}
