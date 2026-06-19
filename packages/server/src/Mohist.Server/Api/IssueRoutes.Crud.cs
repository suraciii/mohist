using Microsoft.AspNetCore.Routing;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.Issue.Services.Attachments;
using Mohist.Server.Project.Services;
using IssueDomain = Mohist.Server.Issue.Domain;

namespace Mohist.Server.Api;

public static partial class IssueRoutes
{
    internal static void MapIssueCrud(this RouteGroupBuilder group)
    {
        group.MapGet("/", async (
            HttpContext ctx,
            string projectRef,
            string? stage,
            string[]? label,
            string? priority,
            bool? archived,
            bool? all,
            IssueQuerier issuesQuery) =>
        {
            var project = GetRequiredProject(ctx);
            if (TryValidateLabelFilters(label, out var labelError) is false)
                return ApiResults.BadRequest(labelError!, "invalid_label");

            var list = await issuesQuery.ListWithLabelFiltersAsync(project.Id, project, stage, label, priority, archived, all);
            return ApiResults.Ok(list);
        });

        group.MapPost("/", async (
            HttpContext ctx,
            string projectRef,
            CreateIssueRequest req,
            IGrainFactory grains,
            IssueQuerier issuesQuery,
            IssueRepositoryResolver repositoryResolver) =>
        {
            if (string.IsNullOrWhiteSpace(req.Title))
                return ApiResults.BadRequest("title is required");

            var project = GetRequiredProject(ctx);

            if (TryValidateLabels(req.Labels, out var labelError) is false)
                return ApiResults.BadRequest(labelError!, "invalid_label");

            var resolution = repositoryResolver.Resolve(project, req.RepositoryName);
            if (resolution.HasProblem)
                return ApiResults.BadRequest(resolution.Problem!.Message, IssueRepositoryResolutionHelpers.RepositoryProblemCodeToApiCode(resolution.Problem.Code));

            var counter = grains.GetGrain<IIssueCounterGrain>(GrainKey.IssueCounter(project.Id));
            var number = await counter.NextAsync();
            var issueId = $"issue_{Guid.NewGuid():N}";
            var issueGrain = grains.GetGrain<IIssueGrain>(GrainKey.Issue(issueId));
            try
            {
                await issueGrain.CreateAsync(
                    project.Id,
                    number,
                    req.Title,
                    req.Body,
                    req.Labels,
                    req.Priority,
                    resolution.Repository!.Name,
                    issueId,
                    req.Risk,
                    req.IsDraft ?? true,
                    req.AttachmentIds);
            }
            catch (AttachmentLimitException ex)
            {
                return ApiResults.Fail(ex.Message, 413, "attachment_count_limit_exceeded");
            }
            catch (AttachmentValidationException ex)
            {
                return ApiResults.BadRequest(ex.Message, "invalid_attachment");
            }
            var issue = await issuesQuery.GetAsync(project.Id, number, project);
            return Results.Json(new { success = true, data = issue }, statusCode: 201);
        });

        group.MapGet("/{number:int}", async (
            HttpContext ctx,
            string projectRef,
            int number,
            IssueQuerier issuesQuery) =>
        {
            var project = GetRequiredProject(ctx);
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
            IssueQuerier issuesQuery) =>
        {
            var project = GetRequiredProject(ctx);

            if (TryValidateLabels(req.Labels, out var labelError) is false)
                return ApiResults.BadRequest(labelError!, "invalid_label");

            var grain = await GetIssueGrainAsync(grains, issueIdentityResolver, project.Id, number);
            if (grain is null) return ApiResults.NotFound($"Issue #{number} not found");
            try
            {
                await grain.UpdateFullAsync(new UpdateIssueData(
                    req.Title, req.Body, req.Labels, req.Priority, req.IsDraft, req.AttachmentIds));
            }
            catch (InvalidOperationException ex)
            {
                return ApiResults.Conflict(ex.Message);
            }
            catch (AttachmentLimitException ex)
            {
                return ApiResults.Fail(ex.Message, 413, "attachment_count_limit_exceeded");
            }
            catch (AttachmentValidationException ex)
            {
                return ApiResults.BadRequest(ex.Message, "invalid_attachment");
            }
            var info = await issuesQuery.GetAsync(project.Id, number);
            return ApiResults.Ok(info);
        });
    }

    internal static bool TryValidateLabels(Dictionary<string, string>? labels, out string? error)
    {
        if (labels is null) { error = null; return true; }
        foreach (var (key, value) in labels)
        {
            try
            {
                IssueDomain.Issue.ValidateLabelKey(key);
                IssueDomain.Issue.ValidateLabelValue(value);
            }
            catch (ArgumentException ex)
            {
                error = ex.Message;
                return false;
            }
        }
        error = null;
        return true;
    }

    internal static bool TryValidateLabelFilters(IReadOnlyList<string>? labels, out string? error)
    {
        if (labels is null || labels.Count == 0) { error = null; return true; }

        foreach (var token in labels)
        {
            var idx = token.IndexOf('=');
            if (idx < 0)
            {
                error = $"Issue label filter '{token}' is invalid; expected key=value";
                return false;
            }

            try
            {
                IssueDomain.Issue.ValidateLabelKey(token[..idx]);
                IssueDomain.Issue.ValidateLabelValue(token[(idx + 1)..]);
            }
            catch (ArgumentException ex)
            {
                error = ex.Message;
                return false;
            }
        }

        error = null;
        return true;
    }
}
