using Microsoft.AspNetCore.Http;
using Mohist.Server.Auth.Domain;
using Mohist.Server.Auth.Identity;
using Mohist.Server.Project.Services;

namespace Mohist.Server.Api;

/// <summary>
/// Integration token management: issue and revoke inbound-integration
/// credentials (docs/auth.md "入站集成：独立令牌"). An integration token
/// is narrowed to one project and carries the <c>webhook</c> scope; the
/// full value appears in exactly one response — the issuance response —
/// and the store only ever holds its hash. Unlike PATs this surface is
/// admin-only: integration credentials are issued for the deployment's
/// inbound integrations, not for the calling principal's own use.
/// </summary>
public static class IntegrationTokenRoutes
{
    private const int MaxNameLength = 256;

    public static WebApplication MapIntegrationTokenRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/integration-tokens");

        group.MapPost("/", CreateAsync);
        group.MapDelete("/{id}", RevokeAsync);

        return app;
    }

    private static async Task<IResult> CreateAsync(
        HttpContext context,
        CreateIntegrationTokenRequest request,
        ICredentialStore store,
        ProjectRefResolver projects,
        CancellationToken ct)
    {
        if (!TryResolveAdmin(context, out var principal))
            return Forbidden();

        var name = request.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return ApiResults.BadRequest("name is required", "integration_name_required");
        if (name.Length > MaxNameLength)
            return ApiResults.BadRequest($"Integration token name must be at most {MaxNameLength} characters", "integration_name_too_long");
        if (name.Contains('/'))
            return ApiResults.BadRequest("Integration token name must not contain '/'", "integration_name_invalid");

        var projectScope = request.ProjectScope?.Trim();
        if (string.IsNullOrWhiteSpace(projectScope))
            return ApiResults.BadRequest("projectScope is required", "integration_project_required");
        var project = await projects.ResolveAsync(projectScope);
        if (project is null)
            return ApiResults.NotFound("Project not found");

        var result = await store.CreateIntegrationAsync(principal.Id, name, project.Id, ct).ConfigureAwait(false);
        if (result.Status == IntegrationCreateStatus.DuplicateName
            || result.Credential is null
            || result.Token is null)
        {
            return ApiResults.Conflict(
                $"An integration token named '{name}' already exists; revoke it before reusing the name",
                "integration_name_in_use");
        }

        var credential = result.Credential;
        return Results.Json(
            new ApiResponse<IntegrationTokenCreatedResponse>(true, new IntegrationTokenCreatedResponse(
                credential.Id,
                credential.Name!,
                credential.ProjectId!,
                credential.Prefix!,
                result.Token,
                credential.CreatedAt)),
            statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> RevokeAsync(
        HttpContext context,
        string id,
        ICredentialStore store,
        TimeProvider time,
        CancellationToken ct)
    {
        if (!TryResolveAdmin(context, out var principal))
            return Forbidden();

        if (string.IsNullOrWhiteSpace(id))
            return ApiResults.BadRequest("id is required", "integration_id_required");

        var revokedAt = time.GetUtcNow();
        var revoked = await store.RevokeIntegrationAsync(principal.Id, id, revokedAt, ct).ConfigureAwait(false);
        if (!revoked)
            return ApiResults.NotFound($"No integration token with id '{id}'");

        return ApiResults.Ok(new { id, revokedAt });
    }

    private static bool TryResolveAdmin(HttpContext context, out MohistPrincipal principal)
    {
        if (context.Items[MohistPrincipal.HttpContextItemKey] is MohistPrincipal { Kind: PrincipalKind.Admin } resolved)
        {
            principal = resolved;
            return true;
        }

        principal = null!;
        return false;
    }

    private static IResult Forbidden() =>
        Results.Json(
            new ApiResponse<object>(false, Error: "Only the admin principal can manage integration tokens.", Code: "forbidden"),
            statusCode: StatusCodes.Status403Forbidden);
}

public sealed record CreateIntegrationTokenRequest(string? Name, string? ProjectScope);

public sealed record IntegrationTokenCreatedResponse(
    string Id,
    string Name,
    string ProjectId,
    string Prefix,
    string Token,
    DateTimeOffset CreatedAt);
