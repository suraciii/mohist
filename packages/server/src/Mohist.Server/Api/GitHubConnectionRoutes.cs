using System.Text.Json;
using System.Text.Json.Serialization;
using Mohist.Server.GitHub.Domain;
using Mohist.Server.GitHub.Infrastructure;
using Mohist.Server.GitHub.Ports;

namespace Mohist.Server.Api;

public static class GitHubConnectionRoutes
{
    public static WebApplication MapGitHubConnectionRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectRef}/github-connections")
            .AddEndpointFilter<ProjectResolutionEndpointFilter>();

        group.MapPost("", async (HttpContext context, GitHubConnectionCreateRequest request, GitHubConnectionStore store, IGitHubAppClient app, CancellationToken ct) =>
        {
            if (request is null) return ApiResults.BadRequest("request body required");
            if (request.AdditionalProperties is { Count: > 0 })
                return ApiResults.BadRequest($"Unknown GitHub connection option '{request.AdditionalProperties.Keys.First()}'.", "unknown_option");
            var project = context.GetResolvedProject();
            var connection = new GitHubConnection
            {
                Id = $"ghconn_{Guid.NewGuid():N}",
                ProjectId = project.Id,
                Owner = request.Owner ?? string.Empty,
                Repo = request.Repo ?? string.Empty,
                RepositoryName = request.RepositoryName ?? string.Empty,
                Approvers = request.Approvers ?? [],
            };
            try
            {
                var installation = await app.DiscoverInstallationAsync(connection.Owner, connection.Repo, ct);
                var webhookSecret = await store.CreateAsync(connection, installation, ct);
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
            var byRepository = connections.ToDictionary(c => c.RepositoryName, StringComparer.OrdinalIgnoreCase);
            var rows = project.Repositories
                .Select(repository => byRepository.TryGetValue(repository.Name, out var connection)
                    ? ToListDto(connection)
                    : GitHubConnectionListDto.Unconnected(project.Id, repository.Name))
                .ToList();
            rows.AddRange(connections
                .Where(connection => !project.Repositories.Any(repository =>
                    string.Equals(repository.Name, connection.RepositoryName, StringComparison.OrdinalIgnoreCase)))
                .Select(ToListDto));
            return ApiResults.Ok(rows.ToArray());
        });

        group.MapGet("/{connectionId}", async (HttpContext context, string connectionId, GitHubConnectionStore store, CancellationToken ct) =>
        {
            var project = context.GetResolvedProject();
            var connection = await store.GetAsync(project.Id, connectionId, ct);
            return connection is null
                ? ApiResults.NotFound($"GitHub connection '{connectionId}' not found")
                : ApiResults.Ok(ToDto(connection));
        });

        group.MapPost("/{connectionId}/enable", (HttpContext context, string connectionId, GitHubConnectionStore store, GitHubIssueSynchronizationService synchronization, CancellationToken ct) =>
            SetStatusAsync(context, store, synchronization, connectionId, GitHubConnectionStatus.Active, ct));

        group.MapPost("/{connectionId}/disable", (HttpContext context, string connectionId, GitHubConnectionStore store, GitHubIssueSynchronizationService synchronization, CancellationToken ct) =>
            SetStatusAsync(context, store, synchronization, connectionId, GitHubConnectionStatus.Disabled, ct));

        group.MapPatch("/{connectionId}", async (HttpContext context, string connectionId, GitHubConnectionUpdateRequest? request, GitHubConnectionStore store, CancellationToken ct) =>
        {
            if (request is null) return ApiResults.BadRequest("request body required");
            if (request.AdditionalProperties is { Count: > 0 })
                return ApiResults.BadRequest($"Unknown GitHub connection option '{request.AdditionalProperties.Keys.First()}'.", "unknown_option");
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

    private static async Task<IResult> SetStatusAsync(HttpContext context, GitHubConnectionStore store, GitHubIssueSynchronizationService synchronization, string connectionId, string status, CancellationToken ct)
    {
        var project = context.GetResolvedProject();
        try
        {
            var transition = await store.SetStatusWithTransitionAsync(project.Id, connectionId, status, ct);
            if (transition is null)
                return ApiResults.NotFound($"GitHub connection '{connectionId}' not found");
            if (status == GitHubConnectionStatus.Active && transition.Changed)
                await synchronization.ReprojectConnectionAsync(transition.Connection, ct);
            return ApiResults.Ok(ToDto(transition.Connection));
        }
        catch (Exception ex)
        {
            return MapError(ex);
        }
    }

    private static GitHubConnectionListDto ToListDto(GitHubConnection connection) => new(
        connection.Id, connection.ProjectId, connection.Owner, connection.Repo, connection.RepositoryName,
        connection.Approvers.ToArray(), connection.Status, connection.InstallationId, connection.RepositoryNodeId,
        connection.ReconnectRequired, connection.NeedsAttention, connection.NeedsReprojection,
        connection.LastErrorCode is null ? null : new GitHubConnectionErrorDto(
            connection.LastErrorCode, connection.LastErrorDetail, connection.LastErrorAt),
        connection.CreatedAt, connection.UpdatedAt);

    private static GitHubConnectionDto ToDto(GitHubConnection connection, string? webhookSecret = null) => new(
        connection.Id, connection.ProjectId, connection.Owner, connection.Repo, connection.RepositoryName,
        connection.Approvers.ToArray(), connection.Status,
        connection.InstallationId, connection.RepositoryNodeId, connection.ReconnectRequired,
        connection.NeedsAttention, connection.NeedsReprojection,
        connection.LastErrorCode is null ? null : new GitHubConnectionErrorDto(
            connection.LastErrorCode, connection.LastErrorDetail, connection.LastErrorAt),
        webhookSecret, connection.CreatedAt, connection.UpdatedAt);

    private static IResult MapError(Exception exception) => exception switch
    {
        GitHubConnectionValidationException validation => ApiResults.Conflict(validation.Message, validation.Code),
        GitHubConnectionConflictException conflict => ApiResults.Conflict(conflict.Message, conflict.Code),
        GitHubAppNotConfiguredException => ApiResults.Conflict("GitHub App identity is not configured on this Server.", "github_app_not_configured"),
        GitHubAppInstallationException app => ApiResults.Conflict(app.Message, app.Code, app.Details),
        _ => throw exception,
    };
}

public sealed record GitHubConnectionDto(
    string Id, string ProjectId, string Owner, string Repo, string RepositoryName,
    string[] Approvers, string Status,
    string? InstallationId, string? RepositoryNodeId, bool ReconnectRequired,
    bool NeedsAttention, bool NeedsReprojection, GitHubConnectionErrorDto? LastError,
    string? WebhookSecret, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record GitHubConnectionErrorDto(string Code, string? Detail, DateTimeOffset? OccurredAt);

public sealed record GitHubConnectionListDto(
    string? Id, string ProjectId, string? Owner, string? Repo, string RepositoryName,
    string[] Approvers, string Status, string? InstallationId, string? RepositoryNodeId,
    bool ReconnectRequired, bool NeedsAttention, bool NeedsReprojection,
    GitHubConnectionErrorDto? LastError, DateTimeOffset? CreatedAt, DateTimeOffset? UpdatedAt)
{
    public static GitHubConnectionListDto Unconnected(string projectId, string repositoryName) =>
        new(null, projectId, null, null, repositoryName, [], "unconnected", null, null, false, false, false, null, null, null);
}

public sealed record GitHubConnectionCreateRequest(
    string? Owner,
    string? Repo,
    string? RepositoryName,
    string[]? Approvers)
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }
}

public sealed record GitHubConnectionUpdateRequest(string[]? Approvers)
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }
}
