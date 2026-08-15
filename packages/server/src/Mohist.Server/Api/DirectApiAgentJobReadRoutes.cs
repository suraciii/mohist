using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Text.Json.Serialization;
using Mohist.Server.Agent.Services;
using Mohist.Server.Auth.Domain;
using Mohist.Server.Auth.Identity;

namespace Mohist.Server.Api;

/// <summary>
/// The first external Agent API route. It is intentionally limited to the
/// persisted Job-only projection; Session, Input, Turn, output, and event
/// reads require their own projection checkpoints before they can be mapped.
/// </summary>
public static class DirectApiAgentJobReadRoutes
{
    public static WebApplication MapDirectApiAgentJobReadRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/projects/{projectId}")
            .RequireScopes(Scope.Operator, Scope.Readonly)
            .AddEndpointFilter<ExternalAgentProjectGrantEndpointFilter>();

        group.MapGet("/agent-jobs/{jobId}", HandleAsync);
        return app;
    }

    private static async Task<IResult> HandleAsync(
        string projectId,
        string jobId,
        DirectApiAgentJobReadStore reads,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            return ApiResults.Fail("Agent Job not found.", StatusCodes.Status404NotFound, "job_not_found");

        var result = await reads.ReadAsync(projectId, jobId, ct);
        if (result.IsProjectionLag)
        {
            return ApiResults.Fail(
                "The public Job projection has not caught up with its source.",
                StatusCodes.Status503ServiceUnavailable,
                "projection_lag");
        }

        if (result.Snapshot is null)
            return ApiResults.Fail("Agent Job not found.", StatusCodes.Status404NotFound, "job_not_found");

        return ApiResults.Ok(new DirectApiAgentJobReadResponse(
                result.Snapshot.ProjectId,
                result.Snapshot.AgentId,
                result.Snapshot.JobId,
                result.Snapshot.Status,
                result.Snapshot.Outcome,
                result.Snapshot.ReasonCode,
                result.Snapshot.AcceptedAt,
                result.Snapshot.StartedAt,
                result.Snapshot.TerminalAt,
                result.Snapshot.ObservedAt));
    }
}

/// <summary>Strict external Job response; no canonical ledger fields leak here.</summary>
public sealed record DirectApiAgentJobReadResponse(
    string ProjectId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? AgentId,
    string JobId,
    string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Outcome,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ReasonCode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? AcceptedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? StartedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? TerminalAt,
    DateTimeOffset ObservedAt);
