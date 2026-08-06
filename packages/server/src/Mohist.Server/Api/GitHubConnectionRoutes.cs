using Mohist.Server.GitHub.Domain;
using Mohist.Server.GitHub.Infrastructure;

namespace Mohist.Server.Api;

public static class GitHubConnectionRoutes
{
    public static WebApplication MapGitHubConnectionRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectRef}/github-connections")
            .AddEndpointFilter<ProjectResolutionEndpointFilter>();

        group.MapPost("", async (HttpContext context, GitHubConnectionCreateRequest request, GitHubConnectionStore store, CancellationToken ct) =>
        {
            if (request is null) return ApiResults.BadRequest("request body required");
            var project = context.GetResolvedProject();
            var connection = new GitHubConnection
            {
                Id = $"ghconn_{Guid.NewGuid():N}",
                ProjectId = project.Id,
                Owner = request.Owner ?? string.Empty,
                Repo = request.Repo ?? string.Empty,
                IntakeLabel = string.IsNullOrWhiteSpace(request.IntakeLabel) ? GitHubIntakeLabel.Default : request.IntakeLabel.Trim(),
                FeedMode = string.IsNullOrWhiteSpace(request.FeedMode) ? GitHubFeedMode.Start : request.FeedMode,
                Approvers = request.Approvers ?? [],
            };
            try
            {
                var webhookSecret = await store.CreateAsync(connection, ct);
                return Results.Json(
                    new ApiResponse<GitHubConnectionDto>(true, ToDto(connection, webhookSecret)),
                    statusCode: StatusCodes.Status201Created);
            }
            catch (Exception ex)
            {
                return MapError(ex);
            }
        });

        group.MapGet("", async (HttpContext context, GitHubConnectionStore store, CancellationToken ct) =>
        {
            var project = context.GetResolvedProject();
            var connections = await store.ListAsync(project.Id, ct);
            return ApiResults.Ok(connections.Select(c => ToDto(c)).ToArray());
        });

        group.MapGet("/{connectionId}", async (HttpContext context, string connectionId, GitHubConnectionStore store, CancellationToken ct) =>
        {
            var project = context.GetResolvedProject();
            var connection = await store.GetAsync(project.Id, connectionId, ct);
            return connection is null
                ? ApiResults.NotFound($"GitHub connection '{connectionId}' not found")
                : ApiResults.Ok(ToDto(connection));
        });

        group.MapPost("/{connectionId}/enable", (HttpContext context, string connectionId, GitHubConnectionStore store, CancellationToken ct) =>
            SetStatusAsync(context, store, connectionId, GitHubConnectionStatus.Active, ct));

        group.MapPost("/{connectionId}/disable", (HttpContext context, string connectionId, GitHubConnectionStore store, CancellationToken ct) =>
            SetStatusAsync(context, store, connectionId, GitHubConnectionStatus.Disabled, ct));

        group.MapPatch("/{connectionId}", async (HttpContext context, string connectionId, GitHubConnectionUpdateRequest? request, GitHubConnectionStore store, CancellationToken ct) =>
        {
            if (request is null) return ApiResults.BadRequest("request body required");
            var project = context.GetResolvedProject();
            try
            {
                var updated = await store.UpdateApproversAsync(project.Id, connectionId, request.Approvers, ct);
                return updated is null
                    ? ApiResults.NotFound($"GitHub connection '{connectionId}' not found")
                    : ApiResults.Ok(ToDto(updated));
            }
            catch (Exception ex)
            {
                return MapError(ex);
            }
        });

        return app;
    }

    private static async Task<IResult> SetStatusAsync(HttpContext context, GitHubConnectionStore store, string connectionId, string status, CancellationToken ct)
    {
        var project = context.GetResolvedProject();
        try
        {
            var updated = await store.SetStatusAsync(project.Id, connectionId, status, ct);
            return updated is null
                ? ApiResults.NotFound($"GitHub connection '{connectionId}' not found")
                : ApiResults.Ok(ToDto(updated));
        }
        catch (Exception ex)
        {
            return MapError(ex);
        }
    }

    private static GitHubConnectionDto ToDto(GitHubConnection connection, string? webhookSecret = null) => new(
        connection.Id, connection.ProjectId, connection.Owner, connection.Repo, connection.RepositoryName,
        connection.IntakeLabel, connection.FeedMode, connection.Approvers.ToArray(), connection.Status,
        connection.IdentityKind, connection.InstallationId, webhookSecret, connection.CreatedAt, connection.UpdatedAt);

    private static IResult MapError(Exception exception) => exception switch
    {
        GitHubConnectionValidationException validation => ApiResults.Conflict(validation.Message, validation.Code),
        GitHubConnectionConflictException conflict => ApiResults.Conflict(conflict.Message, conflict.Code),
        _ => throw exception,
    };
}

public sealed record GitHubConnectionDto(
    string Id, string ProjectId, string Owner, string Repo, string RepositoryName,
    string IntakeLabel, string FeedMode, string[] Approvers, string Status,
    string IdentityKind, string? InstallationId, string? WebhookSecret,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record GitHubConnectionCreateRequest(
    string? Owner,
    string? Repo,
    string? FeedMode,
    string? IntakeLabel,
    string[]? Approvers);

public sealed record GitHubConnectionUpdateRequest(string[]? Approvers);
