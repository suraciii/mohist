namespace Mohist.Server.Infrastructure.Orleans;

public readonly record struct EpicKey
{
    public string ProjectId { get; }
    public int EpicNumber { get; }

    public EpicKey(string projectId, int epicNumber)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("ProjectId is required and must not be blank.", nameof(projectId));
        if (projectId.Contains(ScopedGrainKeyCodec.Separator))
            throw new ArgumentException(
                $"ProjectId must not contain the scoped grain-key separator '{ScopedGrainKeyCodec.Separator}'.",
                nameof(projectId));
        if (epicNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(epicNumber), epicNumber,
                "EpicNumber must be strictly positive.");

        ProjectId = projectId;
        EpicNumber = epicNumber;
    }

    public string ToGrainKeyString() => ScopedGrainKeyCodec.Format(ProjectId, EpicNumber);

    public override string ToString() => ToGrainKeyString();
}
