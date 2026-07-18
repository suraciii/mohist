using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Domain;
using Mohist.Server.Project.Services;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using YamlDotNet.Core;

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

    internal static async Task<IResult> UpdateIssueWorkflowTemplateAsync(
        string projectId,
        int number,
        IssueTemplateRequest req,
        IssueWorkflowProfileManager issueProfileManager,
        IssueQuerier issuesQuery,
        ProjectQuerier projectsQuery)
    {
        var yaml = req.Yaml ?? req.Template;
        if (!string.IsNullOrWhiteSpace(req.ProjectTemplateId) && !string.IsNullOrWhiteSpace(yaml))
            return ApiResults.BadRequest("Specify either projectTemplateId or yaml, not both");
        if (string.IsNullOrWhiteSpace(req.ProjectTemplateId) && string.IsNullOrWhiteSpace(yaml))
            return ApiResults.BadRequest("Specify either projectTemplateId or yaml");

        var issue = await issuesQuery.GetInfoAsync(projectId, number, await projectsQuery.GetByIdAsync(projectId));
        if (issue is null) return ApiResults.NotFound($"Issue #{number} not found");

        try
        {
            await issueProfileManager.UpdateTemplateAsync(projectId, number, new IssueTemplateUpdateRequest(
                ProjectTemplateId: req.ProjectTemplateId,
                Template: yaml));
        }
        catch (YamlException ex)
        {
            return ApiResults.Fail("YAML syntax error: " + ex.Message, 400, "yaml_syntax");
        }
        catch (InvalidOperationException ex)
        {
            return ApiResults.Fail("Workflow definition error: " + ex.Message, 400, "workflow_shape");
        }

        var response = await BuildIssueWorkflowProfileResponseAsync(projectId, number, issueProfileManager, issuesQuery, projectsQuery);
        return ApiResults.Ok(response!);
    }

    internal static JsonElement? BuildRebaseTaskWith(string baseBranch, Mohist.Server.Workflow.Domain.Run.WorkflowRepositoryContext repository)
    {
        var with = new Dictionary<string, object?>
        {
            ["baseBranch"] = baseBranch,
            ["remote"] = "origin",
            ["repository"] = new Dictionary<string, object?>
            {
                ["name"] = repository.Name,
                ["gitUrl"] = repository.GitUrl,
                ["baseBranch"] = repository.BaseBranch,
            },
        };

        return JsonSerializer.SerializeToElement(with, JSON.Options);
    }

    internal static RecoveryDefinition BuildRebaseRecovery()
    {
        var with = new Dictionary<string, JsonElement?>
        {
            ["session"] = JsonSerializer.SerializeToElement("check"),
            ["prompt"] = JsonSerializer.SerializeToElement("${{ prompts.resolve-rebase-conflicts }}"),
            ["options"] = JsonSerializer.SerializeToElement("${{ vars.agent }}"),
        };
        return new RecoveryDefinition(
            Budget: 2,
            Handlers:
            [
                new RecoveryHandlerDefinition(
                    When: "errorCode=conflict",
                    Tasks:
                    [
                        new TaskDefinition(
                            "recover:resolve-rebase-conflicts",
                            "Resolve rebase conflicts",
                            Uses: "mohist/acp-agent",
                            With: with),
                    ],
                    RetrySelf: false),
            ]);
    }

    internal static async Task<IssueWorkflowProfileResponse?> BuildIssueWorkflowProfileResponseAsync(
        string projectId,
        int number,
        IssueWorkflowProfileManager issueProfileManager,
        IssueQuerier issuesQuery,
        ProjectQuerier projectsQuery)
    {
        var info = await issuesQuery.GetInfoAsync(projectId, number, await projectsQuery.GetByIdAsync(projectId));
        if (info is null) return null;

        var state = await issueProfileManager.GetStateAsync(projectId, number);
        var variables = state.Variables;
        var template = state.Template;
        var yaml = template is null ? null : WorkflowYamlSerializer.ToYaml(template);
        // ProfileId is the unified effective profile id projected by the
        // read model; advanced overrides (custom YAML / project template
        // reference) remain visible via HasCustomTemplate / TemplateSource
        // without rewriting the displayed selection. This is the single
        // source of truth — the same value the issue detail and list
        // surfaces return.
        var profileId = info.WorkflowProfileId;
        var updateMode = template is not null ? "custom" : "reference";
        var templateSource = state.HasCustomTemplate || template is not null
            ? "custom"
            : !string.IsNullOrWhiteSpace(state.SourceTemplateId)
                ? "project"
                : "system";

        return new IssueWorkflowProfileResponse(
            IssueNumber: number,
            ProjectId: projectId,
            SourceTemplateId: state.SourceTemplateId,
            HasCustomTemplate: state.HasCustomTemplate,
            Yaml: yaml,
            WorkflowRunId: info.WorkflowRunId,
            ProfileId: profileId,
            UpdateMode: updateMode,
            Variables: variables,
            UpdatedAt: state.UpdatedAt?.ToString("O") ?? info.UpdatedAt,
            TemplateSource: templateSource);
    }

}
