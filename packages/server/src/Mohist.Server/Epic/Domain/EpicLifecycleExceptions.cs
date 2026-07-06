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

public class EpicNotTerminalException : InvalidOperationException
{
    public string EpicId { get; }
    public string CurrentStatus { get; }

    public EpicNotTerminalException(string epicId, string currentStatus)
        : base($"Epic {epicId} is {currentStatus}; reopen requires a terminal state (done or closed).")
    {
        EpicId = epicId;
        CurrentStatus = currentStatus;
    }
}

public class EpicNotReadyToMarkDoneException : InvalidOperationException
{
    public string EpicId { get; }
    public int OpenLinkedCount { get; }

    public EpicNotReadyToMarkDoneException(string epicId, int openLinkedCount)
        : base($"Epic {epicId} has {openLinkedCount} open linked issue(s); mark done is not allowed.")
    {
        EpicId = epicId;
        OpenLinkedCount = openLinkedCount;
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

public class EpicClosedCannotLinkException : InvalidOperationException
{
    public string EpicId { get; }

    public EpicClosedCannotLinkException(string epicId)
        : base($"Epic {epicId} is closed; reopen before linking issues.")
    {
        EpicId = epicId;
    }
}
