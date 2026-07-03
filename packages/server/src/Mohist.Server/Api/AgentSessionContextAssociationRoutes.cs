using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Mohist.Server.Epic.Services;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Api;

/// <summary>
/// Read-only endpoints that surface the generic <c>agent-launch</c>
/// <see cref="Sessions.Domain.AgentSession"/>s associated with an issue
/// or epic via their recorded <c>agent-launch/issue-number</c> or
/// <c>agent-launch/epic-number</c> context-reference labels
/// (issue-130 T-006). Each returns a lightweight association list where
/// every entry carries the session id, agent id, agent name, status,
/// created timestamp, and a link back to the session summary route.
/// These endpoints are pure reads — they perform no writes and create
/// no scope, mount, supervisor, ownership, or workflow lifecycle.
/// </summary>
public static class AgentSessionContextAssociationRoutes
{
    public static WebApplication MapAgentSessionContextAssociationRoutes(this WebApplication app)
    {
        app.MapIssueAgentSessions();
        app.MapEpicAgentSessions();
        return app;
    }

    private static WebApplication MapIssueAgentSessions(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectRef}/issues/{number}/agent-sessions")
            .AddEndpointFilter<ProjectResolutionEndpointFilter>();

        group.MapGet("/", async (
            HttpContext context,
            string projectRef,
            int number,
            AgentSessionQuerier sessions,
            CancellationToken ct) =>
        {
            var project = context.GetResolvedProject();
            var items = await sessions.ListSessionsByContextRefAsync(
                project.Id,
                projectRef,
                GenericAgentSessionMetadata.IssueNumber,
                number.ToString(),
                ct);
            return ApiResults.Ok(items);
        });

        return app;
    }

    private static WebApplication MapEpicAgentSessions(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectRef}/epics/{epicRef}/agent-sessions")
            .AddEndpointFilter<ProjectResolutionEndpointFilter>();

        group.MapGet("/", async (
            HttpContext context,
            string projectRef,
            string epicRef,
            EpicQuerier epicQuerier,
            AgentSessionQuerier sessions,
            CancellationToken ct) =>
        {
            var project = context.GetResolvedProject();

            // Resolve {epicRef} by number-then-id, mirroring the existing
            // EpicRoutes inline resolver (EpicRoutes.cs:37-41).
            var resolved = int.TryParse(epicRef, out var number)
                ? await epicQuerier.GetByNumberAsync(project.Id, number)
                : await epicQuerier.GetAsync(project.Id, epicRef);
            if (resolved is null)
                return ApiResults.NotFound($"Epic '{epicRef}' not found");

            var epicNumber = resolved.Number?.ToString();
            if (string.IsNullOrWhiteSpace(epicNumber))
                return ApiResults.NotFound($"Epic '{epicRef}' has no number");

            var items = await sessions.ListSessionsByContextRefAsync(
                project.Id,
                projectRef,
                GenericAgentSessionMetadata.EpicNumber,
                epicNumber,
                ct);
            return ApiResults.Ok(items);
        });

        return app;
    }
}
