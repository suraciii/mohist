namespace Mohist.Server.Issue.Domain;

public class Issue
{
    public string Id { get; }
    public string ProjectId { get; }
    public int Number { get; }
    public string Title { get; private set; }
    public string? Body { get; private set; }
    public IssueStatus Status { get; private set; }
    public string[] Labels { get; private set; }
    public string Priority { get; private set; }
    public DateTime CreatedAt { get; }
    public DateTime UpdatedAt { get; private set; }
    public string? WorkflowRunId { get; private set; }

    public Issue(
        string id,
        string projectId,
        int number,
        string title,
        string? body = null,
        string[]? labels = null,
        string priority = "p2")
    {
        Id = id;
        ProjectId = projectId;
        Number = number;
        Title = title;
        Body = body;
        Status = IssueStatus.Draft;
        Labels = labels ?? [];
        Priority = priority;
        CreatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Open()
    {
        if (Status != IssueStatus.Draft)
            throw new InvalidOperationException($"Issue #{Number} is {Status}, only Draft can open");
        Status = IssueStatus.Open;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Close()
    {
        if (Status is IssueStatus.Done or IssueStatus.Archived)
            throw new InvalidOperationException($"Issue #{Number} is {Status}, cannot close");
        Status = IssueStatus.Draft;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Complete()
    {
        if (Status != IssueStatus.Open)
            throw new InvalidOperationException($"Issue #{Number} is {Status}, only Open can complete");
        Status = IssueStatus.Done;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Archive()
    {
        if (Status != IssueStatus.Done)
            throw new InvalidOperationException($"Issue #{Number} is {Status}, only Done can archive");
        Status = IssueStatus.Archived;
        UpdatedAt = DateTime.UtcNow;
    }

    public void UpdateTitle(string title)
    {
        Title = title;
        UpdatedAt = DateTime.UtcNow;
    }

    public void UpdateBody(string? body)
    {
        Body = body;
        UpdatedAt = DateTime.UtcNow;
    }

    public void SetWorkflowRunId(string wrId)
    {
        WorkflowRunId = wrId;
        UpdatedAt = DateTime.UtcNow;
    }
}
