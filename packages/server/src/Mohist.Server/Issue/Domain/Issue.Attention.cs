namespace Mohist.Server.Issue.Domain;

public static partial class IssueExtensions
{
    extension(Issue issue)
    {
        public void RequestAttention(IssueAttention attention)
        {
            issue.Attention = attention;
            if (attention.Reason is IssueAttentionReasons.Blocked or IssueAttentionReasons.WorkflowFailed)
                issue.BlockedReason = attention.Message;
            else
                issue.BlockedReason = null;
            issue.UpdatedAt = DateTime.UtcNow;
        }

        public void ClearAttention()
        {
            issue.Attention = null;
            issue.BlockedReason = null;
            issue.UpdatedAt = DateTime.UtcNow;
        }

        public void SetStageApproval(StageApproval? state)
        {
            issue.StageApproval = state;
            issue.UpdatedAt = DateTime.UtcNow;
        }
    }
}
