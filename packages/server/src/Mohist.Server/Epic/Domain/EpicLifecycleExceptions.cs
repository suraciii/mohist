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

public class EpicPausedCannotMarkDoneException : InvalidOperationException
{
    public string EpicId { get; }

    public EpicPausedCannotMarkDoneException(string epicId)
        : base($"Epic {epicId} is paused; resume to running before marking done.")
    {
        EpicId = epicId;
    }
}

public class EpicPauseRequiresRunningException : InvalidOperationException
{
    public string EpicId { get; }
    public string CurrentStatus { get; }

    public EpicPauseRequiresRunningException(string epicId, string currentStatus)
        : base($"Epic {epicId} is {currentStatus}; pause requires running.")
    {
        EpicId = epicId;
        CurrentStatus = currentStatus;
    }
}

public class EpicStartRequiresIdleException : InvalidOperationException
{
    public string EpicId { get; }
    public string CurrentStatus { get; }

    public EpicStartRequiresIdleException(string epicId, string currentStatus)
        : base($"Epic {epicId} is {currentStatus}; start requires idle.")
    {
        EpicId = epicId;
        CurrentStatus = currentStatus;
    }
}

public class EpicResumeRequiresPausedException : InvalidOperationException
{
    public string EpicId { get; }
    public string CurrentStatus { get; }

    public EpicResumeRequiresPausedException(string epicId, string currentStatus)
        : base($"Epic {epicId} is {currentStatus}; resume requires paused.")
    {
        EpicId = epicId;
        CurrentStatus = currentStatus;
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
