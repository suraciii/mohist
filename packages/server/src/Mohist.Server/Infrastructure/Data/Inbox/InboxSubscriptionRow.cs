namespace Mohist.Server.Infrastructure.Data.Inbox;

public class InboxSubscriptionRow
{
    public string ProjectId { get; set; } = null!;
    public bool WorkflowFailedEnabled { get; set; }
    public bool AgentResultUnconfirmedEnabled { get; set; }
    public bool ApprovalRequestedEnabled { get; set; }
    public bool IssueStartedEnabled { get; set; }
    public bool IssueCompletedEnabled { get; set; }
    public bool AgentResponseFailedEnabled { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
