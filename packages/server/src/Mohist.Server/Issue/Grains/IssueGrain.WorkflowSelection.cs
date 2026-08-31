namespace Mohist.Server.Issue.Grains;

public partial class IssueGrain
{
    private async Task<bool> TryStartWithoutWorkflowAsync(IReadOnlySet<int> undeliveredPrerequisites)
    {
        if (!_issue!.NoWorkflow)
            return false;

        if (_issue.WorkflowRunId is not null)
            _issue.ClearStoppedWorkflow(_issue.WorkflowRunId);
        _issue.StartWithoutWorkflow(undeliveredPrerequisites, _timeProvider.GetUtcNow().UtcDateTime);
        await SaveIssueAsync();
        return true;
    }

    private static void ValidateWorkflowSelection(
        bool hasWorkflowProfile,
        string? workflowProfileId,
        bool hasNoWorkflow,
        bool? noWorkflow)
    {
        if (hasWorkflowProfile && hasNoWorkflow && noWorkflow == true && !string.IsNullOrWhiteSpace(workflowProfileId))
            throw new ArgumentException("No Workflow and an explicit Workflow Profile are mutually exclusive");
    }

    private void ApplyWorkflowSelection(
        bool hasWorkflowProfile,
        string? workflowProfileId,
        bool hasNoWorkflow,
        bool? noWorkflow)
    {
        if (!hasWorkflowProfile && !hasNoWorkflow)
            return;

        _issue!.ReplaceWorkflowProfile(
            hasWorkflowProfile ? workflowProfileId : null,
            hasNoWorkflow && noWorkflow == true);
    }
}
