using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Infrastructure.Serialization;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Domain;
using Mohist.Server.Project.Services;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using YamlDotNet.Core;

namespace Mohist.Server.Api;

public static partial class IssueRoutes
{
    internal static async Task<IIssueGrain?> GetIssueGrainAsync(
        IGrainFactory grains,
        IssueIdentityResolver issueIdentityResolver,
        string projectId,
        int number)
    {
        var issueId = await issueIdentityResolver.GetIdAsync(projectId, number);
        return issueId is null ? null : grains.GetGrain<IIssueGrain>(GrainKey.Issue(issueId));
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
        IssueIdentityResolver issueIdentityResolver,
        IssueQuerier issuesQuery,
        string projectId,
        int number)
    {
        var wrId = (await issuesQuery.GetInfoAsync(projectId, number))?.WorkflowRunId;
        if (!string.IsNullOrWhiteSpace(wrId))
            return wrId;

        var grain = await GetIssueGrainAsync(grains, issueIdentityResolver, projectId, number);
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
            await issueProfileManager.UpdateTemplateAsync(issue.Id, new IssueTemplateUpdateRequest(
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

    internal static string BuildRebaseTaskWith(string baseBranch, RepositoryInfo repository, RuntimeTaskRequest? conflictResolver)
    {
        var with = new Dictionary<string, object?>
        {
            ["baseBranch"] = baseBranch,
            ["repository"] = new Dictionary<string, object?>
            {
                ["name"] = repository.Name,
                ["gitUrl"] = repository.GitUrl,
                ["baseBranch"] = repository.BaseBranch,
            },
        };

        if (conflictResolver?.With is not null || conflictResolver?.Uses is not null)
        {
            with["conflictResolver"] = new Dictionary<string, object?>
            {
                ["title"] = string.IsNullOrWhiteSpace(conflictResolver.Title) ? "Resolve rebase conflicts" : conflictResolver.Title,
                ["with"] = conflictResolver.With,
            };
        }
        else
        {
            with["conflictResolver"] = new Dictionary<string, object?>
            {
                ["title"] = "Resolve rebase conflicts",
                ["with"] = DefaultConflictResolverWith(),
            };
        }

        return JsonSerializer.Serialize(with, WorkflowVariableJson.Options);
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

        var state = await issueProfileManager.GetStateAsync(info.Id);
        var variables = state.Variables;
        var template = state.Template;
        var yaml = template is null ? null : WorkflowYamlSerializer.ToYaml(template);
        var profileId = template?.Id ?? state.SourceTemplateId ?? "mohist/default";
        var updateMode = template is not null ? "Custom" : "Reference";
        var templateSource = state.HasCustomTemplate || template is not null
            ? "custom"
            : !string.IsNullOrWhiteSpace(state.SourceTemplateId)
                ? "project"
                : "system";

        return new IssueWorkflowProfileResponse(
            IssueNumber: number,
            ProjectId: projectId,
            IssueId: info.Id,
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

    private static Dictionary<string, object?> DefaultConflictResolverWith() => new()
    {
        ["description"] = "Resolve git rebase conflicts, stage resolved files, and continue the rebase until it completes.",
    };
}
