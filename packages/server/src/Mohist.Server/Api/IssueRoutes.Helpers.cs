using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Domain;
using Mohist.Server.Project.Services;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Api;

public static partial class IssueRoutes
{
    internal static async Task<IIssueGrain?> GetIssueGrainAsync(
        IGrainFactory grains,
        IssueQuerier issuesQuery,
        string projectId,
        int number)
    {
        var issue = await issuesQuery.GetAsync(projectId, number);
        return issue is null
            ? null
            : grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(projectId, number)));
    }

    /// <summary>
    /// Parses the <c>range</c> query parameter into a day count for
    /// the downstream querier. Returns <c>true</c> when the range is
    /// either omitted/empty (<paramref name="windowDays"/> = null,
    /// per-endpoint back-compat default) or a valid preset;
    /// returns <c>false</c> and populates <paramref name="error"/> with
    /// a 400 result when the value is present but not recognised.
    /// </summary>
    internal static bool TryParseRangeParameter(string? range, out int? windowDays, out IResult? error)
    {
        if (string.IsNullOrWhiteSpace(range))
        {
            windowDays = null;
            error = null;
            return true;
        }

        if (!MetricsRange.TryParse(range, out var days))
        {
            windowDays = null;
            error = ApiResults.BadRequest(
                "Unsupported range value. Accepted values: '7d', '30d', '90d'.",
                "unsupported_range",
                new { range });
            return false;
        }

        windowDays = days;
        error = null;
        return true;
    }

    /// <summary>
    /// Returns the project resolved by <see cref="ProjectResolutionEndpointFilter"/>.
    /// Throws if the filter has not run for the current request — that almost
    /// always means the route group forgot to apply the filter.
    /// </summary>
    /// <remarks>
    /// This helper is a thin wrapper around
    /// <see cref="ProjectResolutionHttpContextExtensions.GetResolvedProject"/>;
    /// we keep it so that IssueRoutes handlers can express "I need the project"
    /// without explicitly taking a dependency on the filter contract.
    /// </remarks>
    internal static ProjectInfo GetRequiredProject(HttpContext context)
        => context.GetResolvedProject();

    internal static async Task<string?> ResolveWorkflowRunIdAsync(
        IGrainFactory grains,
        IssueQuerier issuesQuery,
        string projectId,
        int number)
    {
        var wrId = (await issuesQuery.GetInfoAsync(projectId, number))?.WorkflowRunId;
        if (!string.IsNullOrWhiteSpace(wrId))
            return wrId;

        var grain = await GetIssueGrainAsync(grains, issuesQuery, projectId, number);
        return grain is null ? null : (await grain.GetWorkflowStatusAsync())?.WorkflowRunId;
    }

    internal static JsonElement? BuildRebaseTaskWith(string baseBranch)
    {
        var with = new Dictionary<string, object?>
        {
            ["baseBranch"] = baseBranch,
            ["remote"] = "origin",
        };

        return JsonSerializer.SerializeToElement(with, JSON.Options);
    }

}
