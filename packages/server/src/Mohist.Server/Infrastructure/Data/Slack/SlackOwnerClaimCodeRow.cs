namespace Mohist.Server.Infrastructure.Data.Slack;

public static class SlackOwnerClaimCodeKinds
{
    public const string Initial = "initial";
    public const string Transfer = "transfer";
}

public sealed class SlackOwnerClaimCodeRow
{
    public string Id { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string ConnectionId { get; set; } = string.Empty;
    public string CodeHash { get; set; } = string.Empty;
    public string Kind { get; set; } = SlackOwnerClaimCodeKinds.Initial;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public string? SupersededBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
