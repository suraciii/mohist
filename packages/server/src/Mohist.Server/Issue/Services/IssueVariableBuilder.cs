using System.Text.Json;
using Mohist.Server.Infrastructure.Serialization;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.Workflow.Domain;
using MohistIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.Issue.Services;

/// <summary>
/// Builds the variable bundle persisted on <c>IssueWorkflowProfile</c> at issue
/// start. Layers, lowest priority first, are merged via
/// <see cref="VariableBundle.MergeAll"/>:
///
///   1. Project variables (<c>ProjectWorkflowProfile.Variables</c>) — defaults.
///   2. Issue variables (<c>IssueWorkflowProfile.Variables</c>) — overrides.
///
/// Anything a user patches onto the project (e.g. <c>vars.agent.model</c>) is
/// therefore visible to the dispatch layer unless the issue overrides it. This
/// is the single fix for the issue-#80 dispatch stall where the build agent
/// silently fell through to opencode's local default because <c>vars.agent</c>
/// was never populated.
///
/// The built-in context (<c>mohist</c>, <c>issue</c>, <c>project</c>,
/// <c>repository</c>, <c>workspace</c>,
/// <c>openspecChangeDir</c>) is composed by
/// <c>IssueGrain.BuildIssueVariables</c> and merged on top of this user
/// bundle before dispatch. This class only handles the user-variable
/// layering; it deliberately does not emit a built-in context so the two
/// code paths cannot drift.
/// </summary>
public static class IssueVariableBuilder
{
    public static VariableBundle Build(
        VariableBundle? projectBundle,
        VariableBundle? issueBundle,
        string workflowRunId,
        MohistIssue issue,
        WorkflowProjectContext project)
    {
        // The dispatch contract is owned by IssueGrain.BuildIssueVariables;
        // this builder only layers user/project/issue variables.
        _ = workflowRunId;
        _ = issue;
        _ = project;
        return VariableBundle.MergeAll(projectBundle, issueBundle);
    }
}
