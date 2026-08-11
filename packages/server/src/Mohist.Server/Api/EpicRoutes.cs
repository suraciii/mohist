using Microsoft.AspNetCore.Http;
using Mohist.Server.Epic.Domain;
using Mohist.Server.Epic.Grains;
using Mohist.Server.Epic.Services;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Services;
using Mohist.Server.Issue.Domain;
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

            var number = await grains.GetGrain<IEpicCounterGrain>(GrainKey.EpicCounter(pid)).NextAsync();
            var grain = GetEpicGrain(grains, pid, number);
            var dto = await grain.CreateAsync(pid, number, req.Title, req.Description, req.Priority);
            return Results.Json(new ApiResponse<EpicDto>(true, dto), statusCode: 201);
        });

        group.MapGet("/{number:int}", async (HttpContext context, int number, EpicQuerier queryService) =>
        {
            var pid = context.GetResolvedProject().Id;
            var result = await queryService.GetAsync(pid, number);
            return result is null ? ApiResults.NotFound($"Epic #{number} not found") : ApiResults.Ok(result);
        });

        group.MapPatch("/{number:int}", async (HttpContext context, int number, UpdateEpicRequest req, EpicQuerier queryService, IGrainFactory grains) =>
        {
            var pid = context.GetResolvedProject().Id;

            var resolved = await queryService.GetAsync(pid, number);
            if (resolved is null) return ApiResults.NotFound($"Epic #{number} not found");

            var grain = GetEpicGrain(grains, pid, number);
            var updated = await grain.UpdateAsync(req.Title, req.Description, req.Priority);
            return updated is null ? ApiResults.NotFound($"Epic #{number} not found") : ApiResults.Ok(updated);
        });

        group.MapPost("/{number:int}/issues", async (HttpContext context, int number, EpicIssueRequest req, IGrainFactory grains, IssueQuerier issuesQuery, EpicQuerier queryService) =>
        {
            var pid = context.GetResolvedProject().Id;

            var resolved = await queryService.GetAsync(pid, number);
            if (resolved is null) return ApiResults.NotFound($"Epic #{number} not found");

            var issue = await issuesQuery.GetAsync(pid, req.IssueNumber);
            if (issue is null) return ApiResults.Fail("Issue not found", 404, "ISSUE_NOT_FOUND");

            var grain = GetEpicGrain(grains, pid, number);
            try
            {
                var outcome = await grain.LinkIssueAsync(issue.Number, pid);
                return ApiResults.Ok(new BatchMembershipResponse([outcome]));
            }
            catch (EpicClosedCannotLinkException ex)
            {
                return ApiResults.Conflict(ex.Message, "EPIC_CLOSED_CANNOT_LINK", new { epicNumber = ex.EpicNumber });
            }
            catch (IssueChildCannotJoinEpicException ex)
            {
                return ApiResults.Conflict(ex.Message, "issue_is_sub_issue");
            }
            catch (InvalidOperationException ex)
            {
                if (ex.Message.Contains("already belongs"))
                    return ApiResults.Conflict(ex.Message, "DUPLICATE_EPIC_MEMBERSHIP");
                throw;
            }
        });

        group.MapDelete("/{number:int}/issues/{issueNumber:int}", async (HttpContext context, int number, int issueNumber, IGrainFactory grains, EpicQuerier queryService, IssueQuerier issuesQuery) =>
        {
            var pid = context.GetResolvedProject().Id;
            var resolved = await queryService.GetAsync(pid, number);
            if (resolved is null) return ApiResults.NotFound($"Epic #{number} not found");
            if (await issuesQuery.GetAsync(pid, issueNumber) is null)
                return ApiResults.NotFound($"Issue #{issueNumber} not found");

            var grain = GetEpicGrain(grains, pid, number);
            await grain.UnlinkIssueAsync(issueNumber, pid);
            return ApiResults.Ok(new BatchMembershipResponse([
                BatchMembershipOutcome.Unlinked(issueNumber.ToString(), issueNumber),
            ]));
        });

        group.MapPost("/{number:int}/issues:batch", BatchLinkRouteAsync);
        group.MapPost("/{number:int}/issues:batch-unlink", BatchUnlinkRouteAsync);

        group.MapPost("/{number:int}/done", async (HttpContext context, int number, IGrainFactory grains, EpicQuerier queryService) =>
            await SetStatusRouteAsync(context, number, "done", grains, queryService));
        group.MapPost("/{number:int}/close", async (HttpContext context, int number, IGrainFactory grains, EpicQuerier queryService) =>
            await SetStatusRouteAsync(context, number, "closed", grains, queryService));
        group.MapPost("/{number:int}/start", StartRouteAsync);
        group.MapPost("/{number:int}/pause", PauseRouteAsync);
        group.MapPost("/{number:int}/resume", ResumeRouteAsync);
        group.MapPost("/{number:int}/reopen", ReopenRouteAsync);

        group.MapGet("/{number:int}/events", ListEventsRouteAsync);

        return app;
    }

    private static async Task<IResult> ListEventsRouteAsync(
        HttpContext context,
        int number,
        int? limit,
        EpicQuerier queryService,
        EpicEventQuerier eventQuery)
    {
        var pid = context.GetResolvedProject().Id;
        var resolved = await queryService.GetAsync(pid, number);
        if (resolved is null) return ApiResults.NotFound($"Epic #{number} not found");

        var events = await eventQuery.ListAsync(pid, number, limit ?? 200, context.RequestAborted);
        var response = events.Select(StoredCloudEventDto.From).ToList();
        return ApiResults.Ok(response);
    }

    private static async Task<IResult> SetStatusRouteAsync(HttpContext context, int number, string status, IGrainFactory grains, EpicQuerier queryService)
    {
        var pid = context.GetResolvedProject().Id;
        var resolved = await queryService.GetAsync(pid, number);
        if (resolved is null) return ApiResults.NotFound($"Epic #{number} not found");

        var grain = GetEpicGrain(grains, pid, number);
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

    private static async Task<IResult> StartRouteAsync(HttpContext context, int number, IGrainFactory grains, EpicQuerier queryService)
    {
        var pid = context.GetResolvedProject().Id;
        var resolved = await queryService.GetAsync(pid, number);
        if (resolved is null) return ApiResults.NotFound($"Epic #{number} not found");

        var grain = GetEpicGrain(grains, pid, number);
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

    private static async Task<IResult> PauseRouteAsync(HttpContext context, int number, IGrainFactory grains, EpicQuerier queryService)
    {
        var pid = context.GetResolvedProject().Id;
        var resolved = await queryService.GetAsync(pid, number);
        if (resolved is null) return ApiResults.NotFound($"Epic #{number} not found");

        var body = await ReadPauseRequestAsync(context);
        var grain = GetEpicGrain(grains, pid, number);
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

    private static async Task<IResult> ResumeRouteAsync(HttpContext context, int number, IGrainFactory grains, EpicQuerier queryService)
    {
        var pid = context.GetResolvedProject().Id;
        var resolved = await queryService.GetAsync(pid, number);
        if (resolved is null) return ApiResults.NotFound($"Epic #{number} not found");

        var grain = GetEpicGrain(grains, pid, number);
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

    private static async Task<IResult> ReopenRouteAsync(HttpContext context, int number, IGrainFactory grains, EpicQuerier queryService)
    {
        var pid = context.GetResolvedProject().Id;
        var resolved = await queryService.GetAsync(pid, number);
        if (resolved is null) return ApiResults.NotFound($"Epic #{number} not found");

        var grain = GetEpicGrain(grains, pid, number);
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
        int number,
        IGrainFactory grains,
        EpicQuerier queryService,
        IssueQuerier issuesQuery)
    {
        var pid = context.GetResolvedProject().Id;
        var resolved = await queryService.GetAsync(pid, number);
        if (resolved is null) return ApiResults.NotFound($"Epic #{number} not found");

        var req = await ReadBatchRequestAsync(context);
        if (req is null) return ApiResults.BadRequest("body must be a JSON object with an issueNumbers[] array");
        if (req.IssueNumbers is null || req.IssueNumbers.Count == 0)
            return ApiResults.Ok(new BatchMembershipResponse(Array.Empty<BatchMembershipOutcome>()));

        var issues = await issuesQuery.ListReadModelsAsync(pid, all: true);
        var (resolvedItems, perIdentifier) = ResolveBatchItems(issues, req.IssueNumbers);
        var requestedIdentifiers = req.IssueNumbers.Select(n => n.ToString()).ToArray();

        var grain = GetEpicGrain(grains, pid, number);
        IReadOnlyList<BatchMembershipOutcome> grainOutcomes;
        try
        {
            grainOutcomes = await grain.LinkIssuesAsync(resolvedItems, pid);
        }
        catch (EpicClosedCannotLinkException ex)
        {
            return ApiResults.Conflict(ex.Message, "EPIC_CLOSED_CANNOT_LINK", new { epicNumber = ex.EpicNumber });
        }
        catch (IssueChildCannotJoinEpicException ex)
        {
            return ApiResults.Conflict(ex.Message, "issue_is_sub_issue");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
        {
            return ApiResults.NotFound(ex.Message);
        }

        var outcomes = MergeBatchOutcomes(requestedIdentifiers, perIdentifier, grainOutcomes, isUnlink: false);
        return ApiResults.Ok(new BatchMembershipResponse(outcomes));
    }

    private static async Task<IResult> BatchUnlinkRouteAsync(
        HttpContext context,
        int number,
        IGrainFactory grains,
        EpicQuerier queryService,
        IssueQuerier issuesQuery)
    {
        var pid = context.GetResolvedProject().Id;
        var resolved = await queryService.GetAsync(pid, number);
        if (resolved is null) return ApiResults.NotFound($"Epic #{number} not found");

        var req = await ReadBatchRequestAsync(context);
        if (req is null) return ApiResults.BadRequest("body must be a JSON object with an issueNumbers[] array");
        if (req.IssueNumbers is null || req.IssueNumbers.Count == 0)
            return ApiResults.Ok(new BatchMembershipResponse(Array.Empty<BatchMembershipOutcome>()));

        var issues = await issuesQuery.ListReadModelsAsync(pid, all: true);
        var (resolvedItems, perIdentifier) = ResolveBatchItems(issues, req.IssueNumbers);
        var requestedIdentifiers = req.IssueNumbers.Select(n => n.ToString()).ToArray();

        var grain = GetEpicGrain(grains, pid, number);
        IReadOnlyList<BatchMembershipOutcome> grainOutcomes;
        try
        {
            grainOutcomes = await grain.UnlinkIssuesAsync(resolvedItems, pid);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
        {
            return ApiResults.NotFound(ex.Message);
        }

        var outcomes = MergeBatchOutcomes(requestedIdentifiers, perIdentifier, grainOutcomes, isUnlink: true);
        return ApiResults.Ok(new BatchMembershipResponse(outcomes));
    }

    private static (List<BatchMembershipRequestItem> Resolved, Dictionary<string, BatchMembershipRequestItem> ByIdentifier)
        ResolveBatchItems(IReadOnlyList<IssueReadModel> issues, IReadOnlyList<int> requestedNumbers)
    {
        var byNumber = issues.ToDictionary(i => i.Number);
        var resolved = new List<BatchMembershipRequestItem>(requestedNumbers.Count);
        var byIdentifier = new Dictionary<string, BatchMembershipRequestItem>(StringComparer.Ordinal);
        var seenNumbers = new HashSet<int>();
        foreach (var issueNumber in requestedNumbers)
        {
            var identifier = issueNumber.ToString();
            var item = byNumber.ContainsKey(issueNumber)
                ? new BatchMembershipRequestItem(identifier, issueNumber)
                : new BatchMembershipRequestItem(identifier, 0);
            byIdentifier[identifier] = item;
            if (item.IssueNumber > 0 && seenNumbers.Add(item.IssueNumber))
                resolved.Add(item);
        }
        return (resolved, byIdentifier);
    }

    private static IReadOnlyList<BatchMembershipOutcome> MergeBatchOutcomes(
        IReadOnlyList<string> requestedIdentifiers,
        IReadOnlyDictionary<string, BatchMembershipRequestItem> resolvedItems,
        IReadOnlyList<BatchMembershipOutcome> grainOutcomes,
        bool isUnlink)
    {
        var byIssueNumber = grainOutcomes
            .Where(o => o.IssueNumber.HasValue)
            .GroupBy(o => o.IssueNumber!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        var seenIssueNumbers = new HashSet<int>();
        var results = new List<BatchMembershipOutcome>(requestedIdentifiers.Count);
        foreach (var identifier in requestedIdentifiers)
        {
            if (string.IsNullOrWhiteSpace(identifier)) continue;
            // Unresolved identifier: link reports not-found; unlink is
            // idempotent and reports was-not-a-member per the unlink contract.
            if (!resolvedItems.TryGetValue(identifier, out var item) || item.IssueNumber <= 0)
            {
                results.Add(isUnlink
                    ? new BatchMembershipOutcome(identifier, "was-not-a-member")
                    : BatchMembershipOutcome.NotFound(identifier));
                continue;
            }

            if (byIssueNumber.TryGetValue(item.IssueNumber, out var outcome))
            {
                results.Add(seenIssueNumbers.Add(item.IssueNumber)
                    ? outcome with { Identifier = identifier }
                    : DuplicateOutcome(identifier, item, outcome, isUnlink));
            }
            else
            {
                // Resolved issue that the grain dropped (only possible if
                // the grain returned no entry for it — defensive fallback).
                results.Add(isUnlink
                    ? BatchMembershipOutcome.WasNotAMember(identifier, item.IssueNumber)
                    : BatchMembershipOutcome.NotFound(identifier));
            }
        }

        return results;
    }

    private static BatchMembershipOutcome DuplicateOutcome(
        string identifier,
        BatchMembershipRequestItem item,
        BatchMembershipOutcome firstOutcome,
        bool isUnlink)
    {
        if (isUnlink)
            return BatchMembershipOutcome.WasNotAMember(identifier, item.IssueNumber);
        return firstOutcome.Status == "conflict"
            ? firstOutcome with { Identifier = identifier }
            : firstOutcome with { Identifier = identifier, Status = "already-linked" };
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

    private static IEpicGrain GetEpicGrain(IGrainFactory grains, string projectId, int epicNumber) =>
        grains.GetGrain<IEpicGrain>(GrainKey.Epic(new EpicKey(projectId, epicNumber)));
}

public record EpicCreateRequest(string Title, string? Description, string? Priority);
public record EpicIssueRequest(int IssueNumber);
public record UpdateEpicRequest(string? Title = null, string? Description = null, string? Priority = null);
public record PauseEpicRequest(string? Reason = null);
public record BatchMembershipRequest(IReadOnlyList<int>? IssueNumbers);
public sealed record BatchMembershipResponse(IReadOnlyList<BatchMembershipOutcome> Results);
