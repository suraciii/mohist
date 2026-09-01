using Mohist.Server.Infrastructure;
using Mohist.Server.Issue.Domain.Events;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Domain;
using Mohist.Server.Project.Grains;
using Mohist.Server.Project.Services;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workspace.Grains;

namespace Mohist.Server.Issue.Grains;

public partial class IssueGrain
{
    private async Task<(Mohist.Server.Workflow.Domain.Run.WorkflowRepositoryContext Repository, WorkspaceIdentity Workspace, IssueWorkStartedContext Context, WorkflowProjectContext Project)> PrepareWorkflowStartContextAsync(
        WorkflowProjectContext? project,
        string wrId,
        RepositoryInfo repo)
    {
        var issue = _issue!;
        var projectInfo = await GrainFactory.GetGrain<IProjectGrain>(issue.ProjectId).GetAsync();
        var projectContext = BuildWorkflowProjectContext(issue, project, projectInfo, repo);
        var workspace = BuildWorkspaceIdentity(issue, projectContext, wrId);
        var repositoryContext = new Mohist.Server.Workflow.Domain.Run.WorkflowRepositoryContext(
            Name: repo.Name,
            GitUrl: repo.GitUrl,
            BaseBranch: repo.BaseBranch);
        if (string.IsNullOrWhiteSpace(repositoryContext.BaseBranch))
            throw new InvalidOperationException(
                $"Cannot start workflow: target repository '{repo.Name}' has no configured base branch");

        var resolvedTemplate = await _workflowDefinitionResolver.LoadTemplateAsync(wrId, projectContext.Id, issue.Number);
        var definition = resolvedTemplate.Structure
            ?? throw new InvalidOperationException(WorkflowDefinitionResolver.NoEnabledWorkflowProfileMessage);
        var verificationError = ProjectVerificationCommand.Validate(projectInfo?.VerificationCommand);
        if (WorkflowProfileCatalog.IsSystemProfile(resolvedTemplate.Id) && verificationError is not null)
        {
            if (string.IsNullOrWhiteSpace(projectInfo?.VerificationCommand))
                throw new ProjectVerificationConfigurationMissingException(issue.ProjectId);
            throw new ArgumentException(verificationError, nameof(projectInfo.VerificationCommand));
        }
        if (projectInfo?.VerificationCommand is not null)
            ProjectVerificationCommand.Require(projectInfo.VerificationCommand);

        var availablePrompts = await _workflowPromptResolver.LoadPromptsAsync(
            wrId,
            projectId: issue.ProjectId);
        EnsurePromptsReferencesResolve(
            definition,
            availablePrompts.Select(prompt => prompt.Key).ToHashSet(StringComparer.Ordinal));

        return (repositoryContext, workspace, new IssueWorkStartedContext(
            issue.ProjectId,
            issue.Number,
            issue.Title,
            issue.Priority,
            projectInfo?.VerificationCommand), projectContext);
    }

    private async Task EnsureWorkflowStartResourcesAsync(
        string workflowRunId,
        string workspaceName,
        RepositoryInfo repo,
        WorkflowProjectContext projectContext,
        WorkspaceIdentity workspace)
    {
        var wsGrain = GrainFactory.GetGrain<IWorkspaceGrain>(
            Infrastructure.Orleans.GrainKey.Workspace(_issue!.ProjectId, workspaceName));
        await wsGrain.EnsureIssueWorkspaceAsync(_issue.Number, repo.Name, _timeProvider.GetUtcNow());

        var existingVariables = await _issueVariableStore.GetVariablesAsync(_issue.ProjectId, _issue.Number);
        var issueBundle = IssueVariableBuilder.BuildContextBundle(
            workflowRunId,
            _issue,
            projectContext,
            workspace,
            existingVariables);
        var mergedVariables = VariableBundle.Patch(existingVariables, issueBundle);
        await _issueVariableStore.SetVariablesAsync(_issue.ProjectId, _issue.Number, mergedVariables);
    }

    private async Task EnsureVerificationConfiguredBeforeWorkAsync(string projectId)
    {
        var project = await GrainFactory.GetGrain<IProjectGrain>(projectId).GetAsync();
        var verificationError = ProjectVerificationCommand.Validate(project?.VerificationCommand);
        if (verificationError is null)
            return;

        var template = await _workflowDefinitionResolver.LoadTemplateAsync(
            $"issue-preflight:{projectId}:{_issue!.Number}",
            projectId,
            _issue.Number);
        if (WorkflowProfileCatalog.IsSystemProfile(template.Id))
        {
            if (string.IsNullOrWhiteSpace(project?.VerificationCommand))
                throw new ProjectVerificationConfigurationMissingException(projectId);
            throw new ArgumentException(verificationError, nameof(project.VerificationCommand));
        }
    }

    public async Task EnsureWorkflowStartPreparedAsync(
        string workflowRunId,
        string workspaceName,
        string repositoryName,
        string repositoryGitUrl,
        string repositoryBaseBranch,
        string workspacePath,
        string? workspaceBranch,
        string? workspaceChangeDir)
    {
        EnsureIssue();
        if (_issue!.Status != Domain.IssueStatus.InProgress
            || !string.Equals(_issue.WorkflowRunId, workflowRunId, StringComparison.Ordinal))
            return;

        var repo = new RepositoryInfo
        {
            Name = repositoryName,
            GitUrl = repositoryGitUrl,
            BaseBranch = repositoryBaseBranch,
        };
        var projectInfo = await GrainFactory.GetGrain<IProjectGrain>(_issue.ProjectId).GetAsync();
        var projectContext = BuildWorkflowProjectContext(_issue, null, projectInfo, repo);
        var workspace = new WorkspaceIdentity(workspacePath, workspaceBranch, workspaceChangeDir);
        await EnsureWorkflowStartResourcesAsync(workflowRunId, workspaceName, repo, projectContext, workspace);
    }
}
