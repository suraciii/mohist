using Microsoft.AspNetCore.Http;
using Mohist.Server.Epic.Domain;
using Mohist.Server.Epic.Grains;
using Mohist.Server.Epic.Services;
using Mohist.Server.Issue.Services;
using System.Net.Http.Json;

namespace Mohist.Server.Api;

public static class EpicRoutes
{
    public static WebApplication MapEpicRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectRef}/epics")
            .AddEndpointFilter<ProjectResolutionEndpointFilter>();

        group.MapGet("/", async (HttpContext context, string? search, string? sort, string? dir, EpicQuerier queryService) =>
        {
            var pid = context.GetResolvedProject().Id;
            return ApiResults.Ok(await queryService.ListAsync(pid, search, sort, dir));
        });

        group.MapPost("/", async (HttpContext context, EpicCreateRequest req, IGrainFactory grains) =>
        {
            if (string.IsNullOrWhiteSpace(req.Title)) return ApiResults.BadRequest("title is required");
            var pid = context.GetResolvedProject().Id;

            var tempId = $"epic_{Guid.NewGuid():N}";
            var grain = grains.GetGrain<IEpicGrain>($"{pid}:{tempId}");
            var dto = await grain.CreateAsync(pid, req.Title, req.Description, req.Priority);
            return Results.Json(new ApiResponse<EpicDto>(true, dto), statusCode: 201);
        });

        group.MapGet("/{id}", async (HttpContext context, string id, EpicQuerier queryService) =>
        {
            var pid = context.GetResolvedProject().Id;
            var result = int.TryParse(id, out var number)
                ? await queryService.GetByNumberAsync(pid, number)
                : await queryService.GetAsync(pid, id);
            return result is null ? ApiResults.NotFound($"Epic {id} not found") : ApiResults.Ok(result);
        });

        group.MapPatch("/{id}", async (HttpContext context, string id, UpdateEpicRequest req, EpicQuerier queryService, IGrainFactory grains) =>
        {
            var pid = context.GetResolvedProject().Id;

            var resolved = int.TryParse(id, out var number)
                ? await queryService.GetByNumberAsync(pid, number)
                : await queryService.GetAsync(pid, id);
            if (resolved is null) return ApiResults.NotFound($"Epic {id} not found");

            var grain = grains.GetGrain<IEpicGrain>($"{pid}:{resolved.Id}");
            var updated = await grain.UpdateAsync(req.Title, req.Description, req.Priority);
            return updated is null ? ApiResults.NotFound($"Epic {id} not found") : ApiResults.Ok(updated);
        });

        group.MapPost("/{id}/issues", async (HttpContext context, string id, EpicIssueRequest req, IGrainFactory grains, IssueQuerier issuesQuery, EpicQuerier queryService) =>
        {
            var pid = context.GetResolvedProject().Id;

            var resolved = int.TryParse(id, out var number)
                ? await queryService.GetByNumberAsync(pid, number)
                : await queryService.GetAsync(pid, id);
            if (resolved is null) return ApiResults.NotFound($"Epic {id} not found");
            var resolvedId = resolved.Id;

            var issues = await issuesQuery.ListAsync(pid, all: true);
            var issue = issues.FirstOrDefault(i => i.Id == req.IssueId || i.Number.ToString() == req.IssueId);
            if (issue is null) return ApiResults.Fail("Issue not found", 404, "ISSUE_NOT_FOUND");

            var grain = grains.GetGrain<IEpicGrain>($"{pid}:{resolvedId}");
            try
            {
                await grain.LinkIssueAsync(issue.Id, issue.Number, pid);
            }
            catch (InvalidOperationException ex)
            {
                if (ex.Message.Contains("already belongs"))
                    return ApiResults.Conflict(ex.Message, "DUPLICATE_EPIC_MEMBERSHIP");
                throw;
            }
            return ApiResults.Ok(new { epicId = resolvedId, issueId = issue.Id });
        });

        group.MapDelete("/{id}/issues/{issueId}", async (HttpContext context, string id, string issueId, IGrainFactory grains, EpicQuerier queryService, IssueQuerier issuesQuery) =>
        {
            var pid = context.GetResolvedProject().Id;
            var resolved = int.TryParse(id, out var number)
                ? await queryService.GetByNumberAsync(pid, number)
                : await queryService.GetAsync(pid, id);
            if (resolved is null) return ApiResults.NotFound($"Epic {id} not found");

            // Resolve issueId the same way the link endpoint does: accept either the
            // internal id (issue_xxx) or the issue number. Without this, unlink by
            // number silently no-ops because UnlinkIssueAsync matches on internal id.
            var resolvedIssueId = await ResolveIssueIdAsync(issuesQuery, pid, issueId);
            if (resolvedIssueId is null) return ApiResults.NotFound($"Issue {issueId} not found");

            var grain = grains.GetGrain<IEpicGrain>($"{pid}:{resolved.Id}");
            await grain.UnlinkIssueAsync(resolvedIssueId, pid);
            return ApiResults.Ok(new { epicId = resolved.Id, issueId = resolvedIssueId });
        });

        group.MapPost("/{id}/issues:batch", BatchLinkRouteAsync);
        group.MapPost("/{id}/issues:batch-unlink", BatchUnlinkRouteAsync);

        group.MapPost("/{id}/done", async (HttpContext context, string id, IGrainFactory grains, EpicQuerier queryService) =>
            await SetStatusRouteAsync(context, id, "done", grains, queryService));
        group.MapPost("/{id}/close", async (HttpContext context, string id, IGrainFactory grains, EpicQuerier queryService) =>
            await SetStatusRouteAsync(context, id, "closed", grains, queryService));
        group.MapPost("/{id}/start", StartRouteAsync);
        group.MapPost("/{id}/pause", PauseRouteAsync);
        group.MapPost("/{id}/resume", ResumeRouteAsync);
        group.MapPost("/{id}/reopen", ReopenRouteAsync);

        return app;
    }

    private static async Task<IResult> SetStatusRouteAsync(HttpContext context, string id, string status, IGrainFactory grains, EpicQuerier queryService)
    {
        var pid = context.GetResolvedProject().Id;
        var resolved = int.TryParse(id, out var number)
            ? await queryService.GetByNumberAsync(pid, number)
            : await queryService.GetAsync(pid, id);
        if (resolved is null) return ApiResults.NotFound($"Epic {id} not found");

        var grain = grains.GetGrain<IEpicGrain>($"{pid}:{resolved.Id}");
        try
        {
            var dto = await grain.SetStatusAsync(status);
            return ApiResults.Ok(dto);
        }
        catch (EpicNotReadyToMarkDoneException ex)
        {
            return ApiResults.Conflict(ex.Message, "EPIC_NOT_READY_TO_MARK_DONE", new { openCount = ex.OpenLinkedCount });
        }
        catch (EpicAlreadyTerminalException ex)
        {
            return ApiResults.Conflict(ex.Message, "EPIC_ALREADY_TERMINAL", new { currentStatus = ex.CurrentStatus, requestedStatus = ex.RequestedStatus });
        }
        catch (EpicPausedCannotMarkDoneException ex)
        {
            return ApiResults.Conflict(ex.Message, "EPIC_PAUSED_CANNOT_MARK_DONE");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
        {
            return ApiResults.NotFound(ex.Message);
        }
    }

    private static async Task<IResult> StartRouteAsync(HttpContext context, string id, IGrainFactory grains, EpicQuerier queryService)
    {
        var pid = context.GetResolvedProject().Id;
        var resolved = int.TryParse(id, out var number)
            ? await queryService.GetByNumberAsync(pid, number)
            : await queryService.GetAsync(pid, id);
        if (resolved is null) return ApiResults.NotFound($"Epic {id} not found");

        var grain = grains.GetGrain<IEpicGrain>($"{pid}:{resolved.Id}");
        try
        {
            var dto = await grain.StartAsync();
            return ApiResults.Ok(dto);
        }
        catch (EpicStartRequiresIdleException ex)
        {
            return ApiResults.Conflict(ex.Message, "EPIC_START_REQUIRES_IDLE", new { currentStatus = ex.CurrentStatus });
        }
        catch (EpicAlreadyTerminalException ex)
        {
            return ApiResults.Conflict(ex.Message, "EPIC_ALREADY_TERMINAL", new { currentStatus = ex.CurrentStatus, requestedStatus = ex.RequestedStatus });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
        {
            return ApiResults.NotFound(ex.Message);
        }
    }

    private static async Task<IResult> PauseRouteAsync(HttpContext context, string id, IGrainFactory grains, EpicQuerier queryService)
    {
        var pid = context.GetResolvedProject().Id;
        var resolved = int.TryParse(id, out var number)
            ? await queryService.GetByNumberAsync(pid, number)
            : await queryService.GetAsync(pid, id);
        if (resolved is null) return ApiResults.NotFound($"Epic {id} not found");

        var body = await ReadPauseRequestAsync(context);
        var grain = grains.GetGrain<IEpicGrain>($"{pid}:{resolved.Id}");
        try
        {
            var dto = await grain.PauseAsync(body?.Reason);
            return ApiResults.Ok(dto);
        }
        catch (EpicPauseRequiresRunningException ex)
        {
            return ApiResults.Conflict(ex.Message, "EPIC_NOT_RUNNING", new { currentStatus = ex.CurrentStatus });
        }
        catch (EpicAlreadyTerminalException ex)
        {
            return ApiResults.Conflict(ex.Message, "EPIC_ALREADY_TERMINAL", new { currentStatus = ex.CurrentStatus, requestedStatus = ex.RequestedStatus });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
        {
            return ApiResults.NotFound(ex.Message);
        }
    }

    private static async Task<IResult> ResumeRouteAsync(HttpContext context, string id, IGrainFactory grains, EpicQuerier queryService)
    {
        var pid = context.GetResolvedProject().Id;
        var resolved = int.TryParse(id, out var number)
            ? await queryService.GetByNumberAsync(pid, number)
            : await queryService.GetAsync(pid, id);
        if (resolved is null) return ApiResults.NotFound($"Epic {id} not found");

        var grain = grains.GetGrain<IEpicGrain>($"{pid}:{resolved.Id}");
        try
        {
            var dto = await grain.ResumeAsync();
            return ApiResults.Ok(dto);
        }
        catch (EpicResumeRequiresPausedException ex)
        {
            return ApiResults.Conflict(ex.Message, "EPIC_RESUME_REQUIRES_PAUSED", new { currentStatus = ex.CurrentStatus });
        }
        catch (EpicAlreadyTerminalException ex)
        {
            return ApiResults.Conflict(ex.Message, "EPIC_ALREADY_TERMINAL", new { currentStatus = ex.CurrentStatus, requestedStatus = ex.RequestedStatus });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
        {
            return ApiResults.NotFound(ex.Message);
        }
    }

    private static async Task<IResult> ReopenRouteAsync(HttpContext context, string id, IGrainFactory grains, EpicQuerier queryService)
    {
        var pid = context.GetResolvedProject().Id;
        var resolved = int.TryParse(id, out var number)
            ? await queryService.GetByNumberAsync(pid, number)
            : await queryService.GetAsync(pid, id);
        if (resolved is null) return ApiResults.NotFound($"Epic {id} not found");

        var grain = grains.GetGrain<IEpicGrain>($"{pid}:{resolved.Id}");
        try
        {
            var dto = await grain.ReopenAsync();
            return ApiResults.Ok(dto);
        }
        catch (EpicNotTerminalException ex)
        {
            return ApiResults.Conflict(ex.Message, "EPIC_NOT_TERMINAL", new { currentStatus = ex.CurrentStatus });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
        {
            return ApiResults.NotFound(ex.Message);
        }
    }

    private static async Task<IResult> BatchLinkRouteAsync(
        HttpContext context,
        string id,
        IGrainFactory grains,
        EpicQuerier queryService,
        IssueQuerier issuesQuery)
    {
        var pid = context.GetResolvedProject().Id;
        var resolved = int.TryParse(id, out var number)
            ? await queryService.GetByNumberAsync(pid, number)
            : await queryService.GetAsync(pid, id);
        if (resolved is null) return ApiResults.NotFound($"Epic {id} not found");

        var req = await ReadBatchRequestAsync(context);
        if (req is null) return ApiResults.BadRequest("body must be a JSON object with an issueIds[] array");
        if (req.IssueIds is null || req.IssueIds.Count == 0)
            return ApiResults.Ok(new BatchMembershipResponse(Array.Empty<BatchMembershipOutcome>()));

        var issues = await issuesQuery.ListAsync(pid, all: true);
        // Resolve each unique identifier exactly the way the single-issue
        // route does today: by exact internal-id match, or by issue-number
        // string match. Unresolved identifiers flow through as not-found
        // outcomes. The response carries one outcome per unique requested
        // identifier (duplicate identifier strings in the request are
        // de-duplicated — the issue is linked at most once and the
        // duplicate is not an error).
        var resolvedItems = new List<BatchMembershipRequestItem>(req.IssueIds.Count);
        var perIdentifier = new Dictionary<string, BatchMembershipRequestItem>(StringComparer.Ordinal);
        var seenIdentifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var identifier in req.IssueIds)
        {
            if (string.IsNullOrWhiteSpace(identifier)) continue;
            if (!seenIdentifiers.Add(identifier)) continue;
            var match = issues.FirstOrDefault(i =>
                i.Id == identifier || i.Number.ToString() == identifier);
            if (match is null)
            {
                perIdentifier[identifier] = new BatchMembershipRequestItem(
                    Identifier: identifier, IssueId: "", IssueNumber: 0);
                continue;
            }
            var item = new BatchMembershipRequestItem(
                Identifier: identifier, IssueId: match.Id, IssueNumber: match.Number);
            perIdentifier[identifier] = item;
            resolvedItems.Add(item);
        }

        var grain = grains.GetGrain<IEpicGrain>($"{pid}:{resolved.Id}");
        IReadOnlyList<BatchMembershipOutcome> grainOutcomes;
        try
        {
            grainOutcomes = await grain.LinkIssuesAsync(resolvedItems, pid);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
        {
            return ApiResults.NotFound(ex.Message);
        }

        var outcomes = MergeBatchOutcomes(req.IssueIds, perIdentifier, grainOutcomes);
        return ApiResults.Ok(new BatchMembershipResponse(outcomes));
    }

    private static async Task<IResult> BatchUnlinkRouteAsync(
        HttpContext context,
        string id,
        IGrainFactory grains,
        EpicQuerier queryService,
        IssueQuerier issuesQuery)
    {
        var pid = context.GetResolvedProject().Id;
        var resolved = int.TryParse(id, out var number)
            ? await queryService.GetByNumberAsync(pid, number)
            : await queryService.GetAsync(pid, id);
        if (resolved is null) return ApiResults.NotFound($"Epic {id} not found");

        var req = await ReadBatchRequestAsync(context);
        if (req is null) return ApiResults.BadRequest("body must be a JSON object with an issueIds[] array");
        if (req.IssueIds is null || req.IssueIds.Count == 0)
            return ApiResults.Ok(new BatchMembershipResponse(Array.Empty<BatchMembershipOutcome>()));

        var issues = await issuesQuery.ListAsync(pid, all: true);
        var resolvedItems = new List<BatchMembershipRequestItem>(req.IssueIds.Count);
        var perIdentifier = new Dictionary<string, BatchMembershipRequestItem>(StringComparer.Ordinal);
        var seenIdentifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var identifier in req.IssueIds)
        {
            if (string.IsNullOrWhiteSpace(identifier)) continue;
            if (!seenIdentifiers.Add(identifier)) continue;
            var match = issues.FirstOrDefault(i =>
                i.Id == identifier || i.Number.ToString() == identifier);
            if (match is null)
            {
                perIdentifier[identifier] = new BatchMembershipRequestItem(
                    Identifier: identifier, IssueId: "", IssueNumber: 0);
                continue;
            }
            var item = new BatchMembershipRequestItem(
                Identifier: identifier, IssueId: match.Id, IssueNumber: match.Number);
            perIdentifier[identifier] = item;
            resolvedItems.Add(item);
        }

        var grain = grains.GetGrain<IEpicGrain>($"{pid}:{resolved.Id}");
        IReadOnlyList<BatchMembershipOutcome> grainOutcomes;
        try
        {
            grainOutcomes = await grain.UnlinkIssuesAsync(resolvedItems, pid);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
        {
            return ApiResults.NotFound(ex.Message);
        }

        var outcomes = MergeBatchOutcomes(req.IssueIds, perIdentifier, grainOutcomes);
        return ApiResults.Ok(new BatchMembershipResponse(outcomes));
    }

    private static IReadOnlyList<BatchMembershipOutcome> MergeBatchOutcomes(
        IReadOnlyList<string> requestedIdentifiers,
        IReadOnlyDictionary<string, BatchMembershipRequestItem> resolvedItems,
        IReadOnlyList<BatchMembershipOutcome> grainOutcomes)
    {
        var byIssueId = grainOutcomes
            .Where(o => !string.IsNullOrEmpty(o.IssueId))
            .GroupBy(o => o.IssueId ?? string.Empty, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var notFoundByIdentifier = grainOutcomes
            .Where(o => o.Status == "not-found" && string.IsNullOrEmpty(o.IssueId))
            .GroupBy(o => o.Identifier, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var seenIdentifiers = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<BatchMembershipOutcome>(requestedIdentifiers.Count);
        foreach (var identifier in requestedIdentifiers)
        {
            if (string.IsNullOrWhiteSpace(identifier)) continue;
            if (!seenIdentifiers.Add(identifier)) continue;
            // Unresolved identifier: surface as not-found.
            if (!resolvedItems.TryGetValue(identifier, out var item) || string.IsNullOrEmpty(item.IssueId))
            {
                if (notFoundByIdentifier.TryGetValue(identifier, out var nfe))
                {
                    results.Add(nfe);
                }
                else
                {
                    results.Add(BatchMembershipOutcome.NotFound(identifier));
                }
                continue;
            }

            if (byIssueId.TryGetValue(item.IssueId, out var outcome))
            {
                results.Add(outcome);
            }
            else
            {
                // Resolved issue that the grain dropped (only possible if
                // the grain returned no entry for it — defensive fallback).
                results.Add(BatchMembershipOutcome.NotFound(identifier));
            }
        }

        return results;
    }

    private static async Task<BatchMembershipRequest?> ReadBatchRequestAsync(HttpContext context)
    {
        try
        {
            return await context.Request.ReadFromJsonAsync<BatchMembershipRequest>();
        }
        catch
        {
            return null;
        }
    }

    private static async Task<PauseEpicRequest?> ReadPauseRequestAsync(HttpContext context)
    {
        try
        {
            return await context.Request.ReadFromJsonAsync<PauseEpicRequest>();
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> ResolveIssueIdAsync(IssueQuerier issuesQuery, string projectId, string issueId)
    {
        if (!int.TryParse(issueId, out var issueNumber))
            return issueId;

        var issue = await issuesQuery.GetAsync(projectId, issueNumber);
        return issue?.Id;
    }
}

public record EpicCreateRequest(string Title, string? Description, string? Priority);
public record EpicIssueRequest(string IssueId);
public record UpdateEpicRequest(string? Title = null, string? Description = null, string? Priority = null);
public record PauseEpicRequest(string? Reason = null);
public record BatchMembershipRequest(IReadOnlyList<string>? IssueIds);
public sealed record BatchMembershipResponse(IReadOnlyList<BatchMembershipOutcome> Results);
