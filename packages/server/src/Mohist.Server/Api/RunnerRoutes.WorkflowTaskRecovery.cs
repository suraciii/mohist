using Mohist.Server.Auth.Domain;
using Mohist.Server.Auth.Identity;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Api;

public static partial class RunnerRoutes
{
    private static void MapWorkflowTaskRecoveryRoutes(RouteGroupBuilder group)
    {
        group.MapPost("/workflow-recovery/cleanup-lease", async (
            string runnerId,
            WorkflowTaskCleanupLeaseRequest request,
            IGrainFactory grains,
            CancellationToken ct) =>
        {
            if (request is null || !string.Equals(request.Identity.RunnerId, runnerId, StringComparison.Ordinal))
                return ApiResults.BadRequest("cleanup lease identity does not match the runner", "cleanup_identity_mismatch");

            var result = await grains.GetGrain<IWorkflowGrain>(request.Identity.WorkflowRunId)
                .AcquireWorkflowTaskCleanupLeaseAsync(request);
            return Results.Ok(result);
        });

        group.MapPost("/workflow-recovery/cleanup", async (
            string runnerId,
            WorkflowTaskCleanupOperation operation,
            IGrainFactory grains,
            CancellationToken ct) =>
        {
            if (operation is null || !string.Equals(operation.Identity.RunnerId, runnerId, StringComparison.Ordinal))
                return ApiResults.BadRequest("cleanup identity does not match the runner", "cleanup_identity_mismatch");

            var result = await grains.GetGrain<IWorkflowGrain>(operation.Identity.WorkflowRunId)
                .RecordWorkflowTaskCleanupAsync(operation);
            return Results.Ok(result);
        });

        var adoptionEndpoint = group.MapPost("/workflow-recovery/adopt-task-source-changes", async (
            string runnerId,
            WorkflowTaskSourceAdoptionRequest request,
            IGrainFactory grains,
            CancellationToken ct) =>
        {
            if (request is null || !string.Equals(request.Identity.RunnerId, runnerId, StringComparison.Ordinal))
                return ApiResults.BadRequest("adoption identity does not match the runner", "cleanup_identity_mismatch");

            var result = await grains.GetGrain<IWorkflowGrain>(request.Identity.WorkflowRunId)
                .AuthorizeTaskSourceAdoptionAsync(request);
            return Results.Ok(result);
        });

        adoptionEndpoint.RequireScopes(Scope.Operator);

        var adoptionResultEndpoint = group.MapPost("/workflow-recovery/adoption-result", async (
            string runnerId,
            WorkflowTaskSourceAdoption operation,
            IGrainFactory grains,
            CancellationToken ct) =>
        {
            if (operation is null || !string.Equals(operation.Identity.RunnerId, runnerId, StringComparison.Ordinal))
                return ApiResults.BadRequest("adoption identity does not match the runner", "cleanup_identity_mismatch");

            var result = await grains.GetGrain<IWorkflowGrain>(operation.Identity.WorkflowRunId)
                .RecordTaskSourceAdoptionAsync(operation);
            return Results.Ok(result);
        });
        adoptionResultEndpoint.RequireScopes(Scope.Operator);

        group.MapPost("/workflow-recovery/verification", async (
            string runnerId,
            WorkspaceVerification verification,
            IGrainFactory grains,
            CancellationToken ct) =>
        {
            if (verification is null || !string.Equals(verification.Identity.RunnerId, runnerId, StringComparison.Ordinal))
                return ApiResults.BadRequest("verification identity does not match the runner", "cleanup_identity_mismatch");

            var ack = await grains.GetGrain<IWorkflowGrain>(verification.Identity.WorkflowRunId)
                .ReceiveWorkspaceVerificationAsync(verification);
            return Results.Ok(new { acknowledged = ack == ReportAck.Accepted, ack = ack.ToString().ToLowerInvariant() });
        });

        group.MapPost("/workflow-recovery/fresh-workspace", async (
            string runnerId,
            FreshWorkspaceRecoveryRequest request,
            IGrainFactory grains,
            CancellationToken ct) =>
        {
            if (request is null || !string.Equals(request.Identity.RunnerId, runnerId, StringComparison.Ordinal))
                return ApiResults.BadRequest("fresh workspace identity does not match the runner", "cleanup_identity_mismatch");

            var result = await grains.GetGrain<IWorkflowGrain>(request.Identity.WorkflowRunId)
                .AllocateFreshRecoveryWorkspaceAsync(request.Identity, request.BoundaryFingerprint);
            return Results.Ok(result);
        });
    }
}

public sealed record FreshWorkspaceRecoveryRequest(
    WorkflowTaskExecutionIdentity Identity,
    string BoundaryFingerprint);
