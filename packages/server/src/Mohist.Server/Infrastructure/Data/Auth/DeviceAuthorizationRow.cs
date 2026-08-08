namespace Mohist.Server.Infrastructure.Data.Auth;

public class DeviceAuthorizationRow
{
    public string Id { get; set; } = string.Empty;
    public string DeviceCodeHash { get; set; } = string.Empty;
    public string UserCodeHash { get; set; } = string.Empty;
    public string? ClientName { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? PrincipalId { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
