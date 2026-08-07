namespace Mohist.Server.Auth.Domain;

/// <summary>
/// A one-time runner registration token. Only the SHA-256 hash is ever
/// persisted; the value is not bound to a runner until consumed.
/// </summary>
public sealed record EnrollmentToken(string TokenHash, DateTimeOffset ExpiresAt, DateTimeOffset? ConsumedAt);
