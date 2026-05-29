namespace Mohist.Server.Issue.Domain;

public static partial class IssueExtensions
{
    extension(Issue issue)
    {
        public void RequestAttention(IssueAttention attention)
        {
            issue.Attention = attention;
            issue.UpdatedAt = DateTime.UtcNow;
        }

        public void ClearAttention()
        {
            issue.Attention = null;
            issue.UpdatedAt = DateTime.UtcNow;
        }
    }
}