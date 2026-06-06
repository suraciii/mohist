using Microsoft.AspNetCore.Routing;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Services;

namespace Mohist.Server.Api;

public static partial class IssueRoutes
{
    internal static void MapIssueCrud(this RouteGroupBuilder group)
    {
        group.MapGet("/", async (
            HttpContext ctx,
            string projectRef,
            string? stage,
            string? label,
            string? priority,
            bool? archived,
            bool? all,
            IssueQuerier issuesQuery,
            ProjectRefResolver projects) =>
        {
            var project = GetRequiredProject(ctx, projects, projectRef);
            var list = await issuesQuery.ListAsync(project.Id, project, stage, label, priority, archived, all);
            return ApiResults.Ok(list);
        });

        group.MapPost("/", async (
            HttpContext ctx,
            string projectRef,
            CreateIssueRequest req,
            IGrainFactory grains,
            IssueQuerier issuesQuery,
            ProjectRefResolver projects,
            IssueRepositoryResolver repositoryResolver) =>
        {
            if (string.IsNullOrWhiteSpace(req.Title))
                return ApiResults.BadRequest("title is required");

            var project = GetRequiredProject(ctx, projects, projectRef);

            var resolution = repositoryResolver.Resolve(project, req.RepositoryName);
            if (resolution.HasProblem)
                return ApiResults.BadRequest(resolution.Problem!.Message, IssueRepositoryResolutionHelpers.RepositoryProblemCodeToApiCode(resolution.Problem.Code));

            var counter = grains.GetGrain<IIssueCounterGrain>(GrainKey.IssueCounter(project.Id));
            var number = await counter.NextAsync();
            var issueId = $"issue_{Guid.NewGuid():N}";
            var issueGrain = grains.GetGrain<IIssueGrain>(GrainKey.Issue(issueId));
            await issueGrain.CreateAsync(project.Id, number, req.Title, req.Body, req.Labels, req.Priority, resolution.Repository!.Name, issueId);
            var issue = await issuesQuery.GetAsync(project.Id, number, project);
            return Results.Json(new { success = true, data = issue }, statusCode: 201);
        });

        group.MapGet("/{number:int}", async (
            HttpContext ctx,
            string projectRef,
            int number,
            IssueQuerier issuesQuery,
            ProjectRefResolver projects) =>
        {
            var project = GetRequiredProject(ctx, projects, projectRef);
            var info = await issuesQuery.GetAsync(project.Id, number, project);
            return info is not null ? ApiResults.Ok(info) : ApiResults.NotFound($"Issue #{number} not found");
        });

        group.MapPatch("/{number:int}", async (
            HttpContext ctx,
            string projectRef,
            int number,
            UpdateIssueRequest req,
            IGrainFactory grains,
            IssueIdentityResolver issueIdentityResolver,
            IssueQuerier issuesQuery,
            ProjectRefResolver projects) =>
        {
            var project = GetRequiredProject(ctx, projects, projectRef);

            var grain = await GetIssueGrainAsync(grains, issueIdentityResolver, project.Id, number);
            if (grain is null) return ApiResults.NotFound($"Issue #{number} not found");
            try
            {
                await grain.UpdateFullAsync(new UpdateIssueData(
                    req.Title, req.Body, req.Labels, req.Priority));
                var info = await issuesQuery.GetAsync(project.Id, number);
                return ApiResults.Ok(info);
            }
            catch (InvalidOperationException)
            {
                return ApiResults.NotFound($"Issue #{number} not found");
            }
        });
    }
}
