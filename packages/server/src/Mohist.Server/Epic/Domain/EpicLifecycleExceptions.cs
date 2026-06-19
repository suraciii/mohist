namespace Mohist.Server.Epic.Domain;

public class EpicAlreadyTerminalException : InvalidOperationException
{
    public string CurrentStatus { get; }
    public string RequestedStatus { get; }

    public EpicAlreadyTerminalException(string currentStatus, string requestedStatus)
        : base($"Epic is already {currentStatus}; cannot transition to {requestedStatus}.")
    {
        CurrentStatus = currentStatus;
        RequestedStatus = requestedStatus;
    }
}

public class EpicNotReadyToMarkDoneException : InvalidOperationException
{
    public string EpicId { get; }
    public int UndeliveredCount { get; }

    public EpicNotReadyToMarkDoneException(string epicId, int undeliveredCount)
        : base($"Epic {epicId} has {undeliveredCount} undelivered linked issue(s); mark done is not allowed.")
    {
        EpicId = epicId;
        UndeliveredCount = undeliveredCount;
    }
}

public class EpicDuplicateLinkedIssueException : InvalidOperationException
{
    public int IssueNumber { get; }

    public EpicDuplicateLinkedIssueException(int issueNumber)
        : base($"Issue #{issueNumber} is already linked to this epic.")
    {
        IssueNumber = issueNumber;
    }
}
