using Mohist.Server.GitHub.Infrastructure;
using Mohist.Server.Issue.Services;

namespace Mohist.Server.Api;

public static partial class IssueRoutes
{
    internal static void MapIssueGitHub(this RouteGroupBuilder group)
    {
        group.MapPost("/{number:int}/github/sync", async (
            HttpContext ctx,
            int number,
            GitHubIssueSynchronizationService synchronization,
            IssueQuerier issuesQuery) =>
        {
            var project = GetRequiredProject(ctx);
            try
            {
                await synchronization.SyncAsync(project.Id, number, ct: ctx.RequestAborted);
                return ApiResults.Ok(await issuesQuery.GetAsync(project.Id, number, project));
            }
            catch (GitHubSynchronizationException ex)
            {
                return MapGitHubError(ex);
            }
        });

        group.MapPost("/{number:int}/github/link", async (
            HttpContext ctx,
            int number,
            GitHubIssueLinkRequest? request,
            GitHubIssueSynchronizationService synchronization,
            IssueQuerier issuesQuery) =>
        {
            var project = GetRequiredProject(ctx);
            if (request is null || string.IsNullOrWhiteSpace(request.Repository) || request.Number is not > 0)
                return ApiResults.BadRequest("repository and a positive number are required", "invalid_github_issue");
            if (!TryParseCoordinates(request.Repository, out var owner, out var repo))
                return ApiResults.BadRequest("repository must use the owner/repo form", "invalid_github_repository");

            try
            {
                await synchronization.LinkAsync(project.Id, number, owner, repo, request.Number.Value, ctx.RequestAborted);
                return ApiResults.Ok(await issuesQuery.GetAsync(project.Id, number, project));
            }
            catch (GitHubSynchronizationException ex)
            {
                return MapGitHubError(ex);
            }
        });

        group.MapPost("/{number:int}/github/unlink", async (
            HttpContext ctx,
            int number,
            GitHubIssueSynchronizationService synchronization,
            IssueQuerier issuesQuery) =>
        {
            var project = GetRequiredProject(ctx);
            await synchronization.UnlinkAsync(project.Id, number, ctx.RequestAborted);
            return ApiResults.Ok(await issuesQuery.GetAsync(project.Id, number, project));
        });
    }

    private static bool TryParseCoordinates(string value, out string owner, out string repo)
    {
        owner = string.Empty;
        repo = string.Empty;
        var separator = value.IndexOf('/');
        if (separator <= 0 || separator == value.Length - 1 || value.IndexOf('/', separator + 1) >= 0)
            return false;
        owner = value[..separator].Trim();
        repo = value[(separator + 1)..].Trim();
        return owner.Length > 0 && repo.Length > 0;
    }

    private static IResult MapGitHubError(GitHubSynchronizationException exception) => exception.Code switch
    {
        "issue_not_found" or "github_issue_not_found" => ApiResults.NotFound(exception.Message),
        _ => ApiResults.Conflict(exception.Message, exception.Code),
    };
}

public sealed record GitHubIssueLinkRequest(string? Repository, int? Number);
