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
    public int EpicNumber { get; }
    public string CurrentStatus { get; }

    public EpicNotTerminalException(int epicNumber, string currentStatus)
        : base($"Epic #{epicNumber} is {currentStatus}; reopen requires a terminal state (done or closed).")
    {
        EpicNumber = epicNumber;
        CurrentStatus = currentStatus;
    }
}

public class EpicNotReadyToMarkDoneException : InvalidOperationException
{
    public int EpicNumber { get; }
    public int OpenLinkedCount { get; }

    public EpicNotReadyToMarkDoneException(int epicNumber, int openLinkedCount)
        : base($"Epic #{epicNumber} has {openLinkedCount} open linked issue(s); mark done is not allowed.")
    {
        EpicNumber = epicNumber;
        OpenLinkedCount = openLinkedCount;
    }
}

public class EpicPausedCannotMarkDoneException : InvalidOperationException
{
    public int EpicNumber { get; }

    public EpicPausedCannotMarkDoneException(int epicNumber)
        : base($"Epic #{epicNumber} is paused; resume to running before marking done.")
    {
        EpicNumber = epicNumber;
    }
}

public class EpicPauseRequiresRunningException : InvalidOperationException
{
    public int EpicNumber { get; }
    public string CurrentStatus { get; }

    public EpicPauseRequiresRunningException(int epicNumber, string currentStatus)
        : base($"Epic #{epicNumber} is {currentStatus}; pause requires running.")
    {
        EpicNumber = epicNumber;
        CurrentStatus = currentStatus;
    }
}

public class EpicStartRequiresIdleException : InvalidOperationException
{
    public int EpicNumber { get; }
    public string CurrentStatus { get; }

    public EpicStartRequiresIdleException(int epicNumber, string currentStatus)
        : base($"Epic #{epicNumber} is {currentStatus}; start requires idle.")
    {
        EpicNumber = epicNumber;
        CurrentStatus = currentStatus;
    }
}

public class EpicResumeRequiresPausedException : InvalidOperationException
{
    public int EpicNumber { get; }
    public string CurrentStatus { get; }

    public EpicResumeRequiresPausedException(int epicNumber, string currentStatus)
        : base($"Epic #{epicNumber} is {currentStatus}; resume requires paused.")
    {
        EpicNumber = epicNumber;
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
    public int EpicNumber { get; }

    public EpicClosedCannotLinkException(int epicNumber)
        : base($"Epic #{epicNumber} is closed; reopen before linking issues.")
    {
        EpicNumber = epicNumber;
    }
}
