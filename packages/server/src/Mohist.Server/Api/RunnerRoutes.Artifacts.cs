using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services.Artifacts;
using Mohist.Server.Workflow.Storage;

namespace Mohist.Server.Api;

public static partial class RunnerRoutes
{
    private static void MapRunnerArtifactProvisioningRoutes(RouteGroupBuilder group)
    {
        group.MapGet(
            "/workflow-runs/{workflowRunId}/work/{workId}/workspace-artifacts",
            async (
                string runnerId,
                string workflowRunId,
                string workId,
                IGrainFactory grains,
                IWorkflowArtifactQuerier artifacts,
                CancellationToken ct) =>
            {
                var authorization = await AuthorizeWorkflowWorkAsync(
                    grains,
                    runnerId,
                    workflowRunId,
                    workId);
                if (authorization is not null) return authorization;

                var latest = await artifacts.ListLatestAsync(workflowRunId, ct);
                var result = new List<RunnerWorkspaceArtifactDto>(latest.Count);
                foreach (var artifact in latest)
                {
                    if (!WorkflowArtifactPathPolicy.TryNormalizeProvisionPath(
                            artifact.Path,
                            out var path,
                            out _))
                    {
                        continue;
                    }

                    result.Add(new RunnerWorkspaceArtifactDto(
                        artifact.ArtifactId,
                        path,
                        artifact.Kind,
                        artifact.ContentType,
                        artifact.ContentHash,
                        artifact.Size));
                }

                return ApiResults.Ok(new RunnerWorkspaceArtifactListDto(result));
            });

        group.MapGet(
            "/workflow-runs/{workflowRunId}/work/{workId}/workspace-artifacts/{artifactId}/content",
            async (
                string runnerId,
                string workflowRunId,
                string workId,
                string artifactId,
                string? file,
                IGrainFactory grains,
                IWorkflowArtifactQuerier artifacts,
                IWorkflowArtifactStorage storage,
                CancellationToken ct) =>
            {
                var authorization = await AuthorizeWorkflowWorkAsync(
                    grains,
                    runnerId,
                    workflowRunId,
                    workId);
                if (authorization is not null) return authorization;

                var artifact = await artifacts.GetArtifactAsync(workflowRunId, artifactId, ct);
                if (artifact is null
                    || !WorkflowArtifactPathPolicy.TryNormalizeProvisionPath(
                        artifact.Path,
                        out var normalizedPath,
                        out _))
                {
                    return ApiResults.NotFound("Workspace artifact not found");
                }

                if (artifact.Kind == "file")
                {
                    if (!string.IsNullOrWhiteSpace(file))
                        return ApiResults.BadRequest("file is only valid for directory artifacts");
                    try
                    {
                        return Results.Stream(
                            storage.OpenFileContent(artifact.ArtifactStoragePath),
                            artifact.ContentType ?? "application/octet-stream");
                    }
                    catch (WorkflowArtifactNotFoundException)
                    {
                        return ApiResults.NotFound("Workspace artifact content is missing");
                    }
                }

                if (artifact.Kind != "directory")
                    return ApiResults.Fail("Unknown workspace artifact kind", 500, "unknown_artifact_kind");

                if (!string.IsNullOrWhiteSpace(file))
                {
                    try
                    {
                        return Results.Stream(
                            storage.OpenDirectoryEntry(artifact.ArtifactStoragePath, file),
                            "application/octet-stream");
                    }
                    catch (WorkflowArtifactNotFoundException)
                    {
                        return ApiResults.NotFound("Workspace artifact file is missing");
                    }
                }

                try
                {
                    var listing = await storage.ListDirectoryEntriesAsync(
                        artifact.ArtifactStoragePath,
                        ct);
                    return ApiResults.Ok(new RunnerWorkspaceArtifactDirectoryDto(
                        artifact.ArtifactId,
                        normalizedPath,
                        listing.Entries.Select(entry => new RunnerWorkspaceArtifactDirectoryEntryDto(
                            entry.RelativePath,
                            entry.Size,
                            entry.ContentHash,
                            entry.ContentType)).ToList(),
                        listing.TotalSize));
                }
                catch (WorkflowArtifactNotFoundException)
                {
                    return ApiResults.NotFound("Workspace artifact directory is missing");
                }
            });
    }

    private static async Task<IResult?> AuthorizeWorkflowWorkAsync(
        IGrainFactory grains,
        string runnerId,
        string workflowRunId,
        string workId)
    {
        if (string.IsNullOrWhiteSpace(workflowRunId) || string.IsNullOrWhiteSpace(workId))
            return ApiResults.BadRequest("workflowRunId and workId are required");

        var workflow = grains.GetGrain<IWorkflowGrain>(workflowRunId);
        var assignedRunnerId = await workflow.GetAssignedWorkerIdAsync();
        if (!string.Equals(assignedRunnerId, runnerId, StringComparison.Ordinal))
        {
            return ApiResults.Fail(
                "WorkflowRun is not assigned to this Runner",
                StatusCodes.Status403Forbidden,
                "workflow_runner_not_assigned");
        }

        var activeWork = await workflow.GetActiveWorkAsync(workId);
        if (activeWork is null
            || (activeWork.OwnerWorkflowRunId is not null
                && !string.Equals(activeWork.OwnerWorkflowRunId, workflowRunId, StringComparison.Ordinal)))
        {
            return ApiResults.NotFound("Active workflow work item not found");
        }

        return null;
    }
}

internal sealed record RunnerWorkspaceArtifactListDto(
    IReadOnlyList<RunnerWorkspaceArtifactDto> Artifacts);

internal sealed record RunnerWorkspaceArtifactDto(
    string ArtifactId,
    string Path,
    string Kind,
    string? ContentType,
    string? ContentHash,
    long? Size);

internal sealed record RunnerWorkspaceArtifactDirectoryDto(
    string ArtifactId,
    string Path,
    IReadOnlyList<RunnerWorkspaceArtifactDirectoryEntryDto> Entries,
    long TotalSize);

internal sealed record RunnerWorkspaceArtifactDirectoryEntryDto(
    string RelativePath,
    long Size,
    string? ContentHash,
    string? ContentType);
