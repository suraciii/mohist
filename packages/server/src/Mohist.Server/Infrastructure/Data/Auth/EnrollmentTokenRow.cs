namespace Mohist.Server.Infrastructure.Data.Auth;

/// <summary>
/// One-time runner enrollment token. Only the SHA-256 hash of the token
/// value is ever stored; <see cref="ConsumedAt"/> makes consumption
/// atomic and single-use.
/// </summary>
public class EnrollmentTokenRow
{
    public string Id { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
