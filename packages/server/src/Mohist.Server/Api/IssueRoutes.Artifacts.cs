using Microsoft.AspNetCore.Routing;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.Workflow.Services.Artifacts;
using Mohist.Server.Workflow.Storage;

namespace Mohist.Server.Api;

public static partial class IssueRoutes
{
    internal static void MapIssueWorkflowArtifacts(this RouteGroupBuilder group)
    {
        group.MapGet("/{number:int}/workflow/artifacts", async (
            HttpContext ctx,
            int number,
            string? path,
            bool? history,
            string? taskRunId,
            IGrainFactory grains,
            IssueQuerier issuesQuery,
            IWorkflowArtifactQuerier artifactsQuery) =>
        {
            var project = GetRequiredProject(ctx);

            var wrId = await ResolveWorkflowRunIdAsync(grains, issuesQuery, project.Id, number);
            if (wrId is null)
                return ApiResults.Ok(Array.Empty<WorkflowArtifactDto>());

            var ct = ctx.RequestAborted;

            IReadOnlyList<WorkflowArtifactInfo> artifacts;
            if (!string.IsNullOrWhiteSpace(taskRunId))
            {
                artifacts = await artifactsQuery.ListByTaskRunAsync(wrId, taskRunId, ct);
            }
            else if (!string.IsNullOrWhiteSpace(path) && history == true)
            {
                artifacts = await artifactsQuery.ListHistoryAsync(wrId, path, ct);
            }
            else if (!string.IsNullOrWhiteSpace(path))
            {
                // Single-path query without history returns only the
                // newest version for that path, mirroring the natural
                // reading of "path" without "history".
                artifacts = await artifactsQuery.ListLatestByPathAsync(wrId, path, ct);
            }
            else
            {
                artifacts = await artifactsQuery.ListLatestAsync(wrId, ct);
            }

            var dtos = artifacts.Select(ToArtifactDto).ToList();
            return ApiResults.Ok(dtos);
        });

        group.MapGet("/{number:int}/workflow/artifacts/{artifactId}/content", async (
            HttpContext ctx,
            int number,
            string artifactId,
            string? file,
            IGrainFactory grains,
            IssueQuerier issuesQuery,
            IWorkflowArtifactQuerier artifactsQuery,
            IWorkflowArtifactStorage storage) =>
        {
            var project = GetRequiredProject(ctx);

            var wrId = await ResolveWorkflowRunIdAsync(grains, issuesQuery, project.Id, number);
            if (wrId is null)
                return ApiResults.NotFound($"Issue #{number} has no workflow run");

            var ct = ctx.RequestAborted;
            var artifact = await artifactsQuery.GetArtifactAsync(wrId, artifactId, ct);

            if (artifact is null)
                return ApiResults.NotFound($"Artifact '{artifactId}' not found in issue #{number} workflow context");

            if (artifact.Kind == "file")
            {
                try
                {
                    var content = storage.OpenFileContent(artifact.ArtifactStoragePath);
                    var contentType = artifact.ContentType ?? "application/octet-stream";
                    return Results.Stream(content, contentType);
                }
                catch (WorkflowArtifactNotFoundException)
                {
                    return ApiResults.NotFound("Recorded artifact content is missing");
                }
                catch (WorkflowArtifactStorageException ex)
                {
                    return ApiResults.Fail(ex.Message, 500, "artifact_storage_error");
                }
            }

            if (artifact.Kind == "directory")
            {
                if (!string.IsNullOrWhiteSpace(file))
                {
                    try
                    {
                        var content = storage.OpenDirectoryEntry(artifact.ArtifactStoragePath, file);
                        return Results.Stream(content, "application/octet-stream");
                    }
                    catch (WorkflowArtifactNotFoundException)
                    {
                        return ApiResults.NotFound($"Recorded file '{file}' not found in directory artifact");
                    }
                    catch (WorkflowArtifactStorageException ex)
                    {
                        return ApiResults.Fail(ex.Message, 500, "artifact_storage_error");
                    }
                }

                try
                {
                    var listing = await storage.ListDirectoryEntriesAsync(artifact.ArtifactStoragePath, ct);
                    return ApiResults.Ok(new WorkflowArtifactDirectoryDto
                    {
                        artifactId = artifact.ArtifactId,
                        path = artifact.Path,
                        displayName = artifact.DisplayName,
                        kind = "directory",
                        recordedAt = artifact.RecordedAt.ToString("o"),
                        entries = listing.Entries.Select(e => new WorkflowArtifactDirectoryEntryDto
                        {
                            relativePath = e.RelativePath,
                            size = e.Size,
                            contentType = e.ContentType,
                        }).ToList(),
                        totalSize = listing.TotalSize,
                    });
                }
                catch (WorkflowArtifactNotFoundException)
                {
                    return ApiResults.NotFound("Recorded directory artifact content is missing");
                }
                catch (WorkflowArtifactStorageException ex)
                {
                    return ApiResults.Fail(ex.Message, 500, "artifact_storage_error");
                }
            }

            return ApiResults.Fail($"Unknown artifact kind '{artifact.Kind}'", 500, "unknown_artifact_kind");
        });
    }

    private static WorkflowArtifactDto ToArtifactDto(WorkflowArtifactInfo info) => new()
    {
        artifactId = info.ArtifactId,
        workflowRunId = info.WorkflowRunId,
        taskRunId = info.TaskRunId,
        path = info.Path,
        kind = info.Kind,
        contentType = info.ContentType,
        size = info.Size,
        recordedAt = info.RecordedAt.ToString("o"),
        displayName = info.DisplayName,
    };
}
