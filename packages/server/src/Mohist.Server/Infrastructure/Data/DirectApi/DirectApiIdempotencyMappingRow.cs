namespace Mohist.Server.Infrastructure.Data.DirectApi;

/// <summary>
/// Durable request fence for the direct API write commands. The composite
/// command/scope key is the caller-visible idempotency grain; the outcome is
/// internal JSON containing canonical identities and command-specific state.
/// </summary>
public sealed class DirectApiIdempotencyMappingRow
{
    public string Command { get; set; } = string.Empty;
    public string ScopeKey { get; set; } = string.Empty;
    public string CallerKeyId { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string? Outcome { get; set; }
    public string? FrozenTarget { get; set; }
    public string? TurnId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
