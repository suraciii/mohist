using System.Text.Json;
using Mohist.Server.Infrastructure;
using Mohist.Server.Workflow.Domain;

namespace Mohist.Server.Workflow.Services;

/// <summary>
/// issue-417 T-006 (D4): authoritative routing overlay applied
/// after every configurable variable layer (template, global,
/// project, issue, run, stage). Two goals:
/// <list type="bullet">
///   <item>Replace the entire <c>repository</c> and <c>workspace</c>
///     roots from the run's persisted snapshot, so configurable
///     variables can never redirect repository or workspace routing
///     to another target.</item>
///   <item>Re-assert run identity (<c>mohist.runId</c>,
///     <c>project.id</c>, <c>issue.number</c>) so a stale
///     configurable layer cannot override those values mid-run.</item>
/// </list>
/// The overlay is applied at the very end of variable resolution
/// (dispatch and display paths) by
/// <see cref="WorkflowProfileManager.ResolveEffectiveVariablesAsync"/>
/// (extended in T-006). It is intentionally narrow: title, body,
/// prompts, model, CI, and ordinary user variables are NOT touched.
/// </summary>
public static class AuthoritativeRoutingOverlay
{
    public static VariableBundle Build(
        string workflowRunId,
        Mohist.Server.Workflow.Domain.Run.WorkflowRepositoryContext? repository,
        Mohist.Server.Workflow.Domain.Run.WorkspaceIdentity? workspace,
        string? projectId,
        int? issueNumber)
    {
        var variables = new Dictionary<string, JsonElement?>(StringComparer.Ordinal)
        {
            ["mohist"] = JSON.SerializeToElement(new
            {
                runId = workflowRunId,
            }),
        };

        if (projectId is not null || issueNumber.HasValue)
        {
            variables["project"] = JSON.SerializeToElement(new
            {
                id = projectId ?? string.Empty,
            });
        }

        if (issueNumber.HasValue)
        {
            variables["issue"] = JSON.SerializeToElement(new
            {
                number = issueNumber.Value,
            });
        }

        if (repository is not null)
        {
            variables["repository"] = JSON.SerializeToElement(new
            {
                name = repository.Name,
                gitUrl = repository.GitUrl,
                baseBranch = repository.BaseBranch,
                remoteFingerprint = repository.RemoteFingerprint,
                remoteIdentityVersion = repository.RemoteIdentityVersion,
            });
        }

        if (workspace is not null)
        {
            variables["workspace"] = JSON.SerializeToElement(new
            {
                path = workspace.Path,
                branch = workspace.Branch ?? string.Empty,
                changeDir = workspace.ChangeDir ?? string.Empty,
            });
        }

        var varsJson = JSON.Serialize(variables);
        var varsElement = JSON.DeserializeElement(varsJson);
        return new VariableBundle(varsElement);
    }

    public static VariableBundle Apply(
        JsonElement? effectiveVariables,
        string workflowRunId,
        Mohist.Server.Workflow.Domain.Run.WorkflowRepositoryContext? repository,
        Mohist.Server.Workflow.Domain.Run.WorkspaceIdentity? workspace,
        string? projectId,
        int? issueNumber)
    {
        var values = effectiveVariables is { ValueKind: JsonValueKind.Object }
            ? effectiveVariables.Value.EnumerateObject()
                .ToDictionary(property => property.Name, property => (JsonElement?)property.Value.Clone(), StringComparer.Ordinal)
            : new Dictionary<string, JsonElement?>(StringComparer.Ordinal);

        var overlay = Build(workflowRunId, repository, workspace, projectId, issueNumber);
        if (overlay.Vars is not { ValueKind: JsonValueKind.Object } overlayVars)
            return new VariableBundle(JSON.SerializeToElement(values));

        foreach (var property in overlayVars.EnumerateObject())
        {
            if (property.Name is "repository" or "workspace")
                values[property.Name] = property.Value.Clone();
            else
                values[property.Name] = VariableJsonMerge.ApplyPatch(
                    values.TryGetValue(property.Name, out var existing) ? existing : null,
                    property.Value);
        }

        return new VariableBundle(JSON.SerializeToElement(values));
    }
}
