namespace Mohist.Server.Infrastructure.Orleans;

public readonly record struct IssueKey
{
    public string ProjectId { get; }
    public int IssueNumber { get; }

    public IssueKey(string projectId, int issueNumber)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("ProjectId is required and must not be blank.", nameof(projectId));
        if (projectId.Contains(ScopedGrainKeyCodec.Separator))
            throw new ArgumentException(
                $"ProjectId must not contain the scoped grain-key separator '{ScopedGrainKeyCodec.Separator}'.",
                nameof(projectId));
        if (issueNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(issueNumber), issueNumber,
                "IssueNumber must be strictly positive.");

        ProjectId = projectId;
        IssueNumber = issueNumber;
    }

    public string ToGrainKeyString() => ScopedGrainKeyCodec.Format(ProjectId, IssueNumber);

    public override string ToString() => ToGrainKeyString();
}
