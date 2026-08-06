namespace Mohist.Server.Slack.Domain;

public static class SlackLeaseTargetKind
{
    public const string Manager = "manager";
    public const string Connection = "connection";
}

public static class SlackLeaseKind
{
    public const string Validation = "validation";
    public const string Runtime = "runtime";
}

/// <summary>
/// Identifies a Socket lease target and its deterministic fencing key.
/// </summary>
public abstract record SlackLeaseTargetRef(string Kind)
{
    public abstract string TargetKey { get; }

    public sealed record Manager(string EnrollmentId, string WorkspaceTeamId)
        : SlackLeaseTargetRef(SlackLeaseTargetKind.Manager)
    {
        public override string TargetKey => $"{SlackLeaseTargetKind.Manager}:{EnrollmentId}";
    }

    public sealed record Connection(string ProjectId, string ConnectionId)
        : SlackLeaseTargetRef(SlackLeaseTargetKind.Connection)
    {
        public override string TargetKey =>
            $"{SlackLeaseTargetKind.Connection}:{ProjectId}:{ConnectionId}";
    }
}
