using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Workspace.Domain;
using Mohist.Server.Workspace.Grains;
using Mohist.Server.Workspace.Services;

namespace Mohist.Server.Api;

/// <summary>
/// Workspace entity routes (named workspaces). Distinct from the issue
/// review workspace routes in <see cref="WorkspaceRoutes"/> which address
/// workflow-run workspaces through <c>/api/projects/.../issues/...</c>.
/// </summary>
public static class WorkspaceEntityRoutes
{
    public static WebApplication MapWorkspaceEntityRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectRef}/workspaces")
            .AddEndpointFilter<ProjectResolutionEndpointFilter>();

        group.MapGet("/", async (
            HttpContext context,
            string projectRef,
            string? status,
            string? origin,
            WorkspaceQuerier querier,
            CancellationToken ct) =>
        {
            var project = context.GetResolvedProject();
            var items = await querier.ListAsync(project.Id, status, origin, ct);
            return ApiResults.Ok(items);
        });

        group.MapGet("/{name}", async (
            HttpContext context,
            string projectRef,
            string name,
            WorkspaceQuerier querier,
            CancellationToken ct) =>
        {
            var project = context.GetResolvedProject();
            var workspace = await querier.GetAsync(project.Id, name, ct);
            return workspace is null
                ? ApiResults.NotFound($"Workspace '{name}' not found")
                : ApiResults.Ok(workspace);
        });

        group.MapPost("/", async (
            HttpContext context,
            string projectRef,
            WorkspaceCreateBody body,
            IGrainFactory grains,
            TimeProvider time,
            CancellationToken ct) =>
        {
            var project = context.GetResolvedProject();
            if (body is null || string.IsNullOrWhiteSpace(body.Name))
                return ApiResults.BadRequest("Workspace name is required.", "workspace_name_invalid");

            try
            {
                var workspace = await grains.GetGrain<IWorkspaceGrain>(
                        GrainKey.Workspace(project.Id, body.Name.Trim()))
                    .CreateManualAsync(body.Name, (body.Repos ?? []).ToArray(), time.GetUtcNow());
                return ApiResults.Ok(workspace);
            }
            catch (WorkspaceDomainException ex)
            {
                return WorkspaceError(ex);
            }
        });

        group.MapPost("/{name}/repo", async (
            HttpContext context,
            string projectRef,
            string name,
            WorkspaceRepoBody body,
            IGrainFactory grains,
            CancellationToken ct) =>
        {
            var project = context.GetResolvedProject();
            if (body is null || string.IsNullOrWhiteSpace(body.Repo))
                return ApiResults.BadRequest("repo is required.", "workspace_repository_required");

            try
            {
                var workspace = await grains.GetGrain<IWorkspaceGrain>(
                        GrainKey.Workspace(project.Id, name))
                    .AddRepositoryAsync(body.Repo);
                return workspace is null
                    ? ApiResults.NotFound($"Workspace '{name}' not found")
                    : ApiResults.Ok(workspace);
            }
            catch (WorkspaceDomainException ex)
            {
                return WorkspaceError(ex);
            }
        });

        group.MapDelete("/{name}/repo", async (
            HttpContext context,
            string projectRef,
            string name,
            string? repo,
            IGrainFactory grains,
            CancellationToken ct) =>
        {
            var project = context.GetResolvedProject();
            if (string.IsNullOrWhiteSpace(repo))
                return ApiResults.BadRequest("repo query parameter is required.", "workspace_repository_required");

            try
            {
                var workspace = await grains.GetGrain<IWorkspaceGrain>(
                        GrainKey.Workspace(project.Id, name))
                    .RemoveRepositoryAsync(repo);
                return workspace is null
                    ? ApiResults.NotFound($"Workspace '{name}' not found")
                    : ApiResults.Ok(workspace);
            }
            catch (WorkspaceDomainException ex)
            {
                return WorkspaceError(ex);
            }
        });

        group.MapPost("/{name}/close", async (
            HttpContext context,
            string projectRef,
            string name,
            IGrainFactory grains,
            TimeProvider time,
            CancellationToken ct) =>
        {
            var project = context.GetResolvedProject();
            try
            {
                var workspace = await grains.GetGrain<IWorkspaceGrain>(
                        GrainKey.Workspace(project.Id, name))
                    .CloseAsync(time.GetUtcNow());
                return workspace is null
                    ? ApiResults.NotFound($"Workspace '{name}' not found")
                    : ApiResults.Ok(workspace);
            }
            catch (WorkspaceDomainException ex)
            {
                return WorkspaceError(ex);
            }
        });

        return app;
    }

    private static IResult WorkspaceError(WorkspaceDomainException ex)
    {
        var (statusCode, code) = ex.Code switch
        {
            "workspace_name_invalid" or "workspace_repository_required"
                or "workspace_repository_duplicate" or "workspace_repository_not_found" => (StatusCodes.Status400BadRequest, ex.Code),
            "workspace_name_taken" or "workspace_origin_conflict" or "workspace_conflict"
                or "workspace_archived" or "workspace_already_archived"
                or "workspace_has_active_sessions" or "workspace_close_not_allowed_for_issue"
                or "workspace_home_claimed" => (StatusCodes.Status409Conflict, ex.Code),
            "workspace_project_not_found" => (StatusCodes.Status404NotFound, ex.Code),
            _ => (StatusCodes.Status400BadRequest, ex.Code),
        };

        var details = ex.Hint is null ? null : new { hint = ex.Hint };
        return ApiResults.Fail(ex.Message, statusCode, code, details);
    }
}

public sealed record WorkspaceCreateBody(string? Name, IReadOnlyList<string>? Repos);

public sealed record WorkspaceRepoBody(string? Repo);