namespace Mohist.Server.Auth.Domain;

/// <summary>
/// Append-only persistence contract for the auth audit trail. Records
/// are written after the fact, so a failed write must never fail the
/// operation that produced the event — emit paths surface failures as
/// logs, never as request errors.
/// </summary>
public interface IAuthAuditEventStore
{
    Task RecordAsync(AuthAuditEvent auditEvent, CancellationToken ct = default);

    /// <summary>
    /// Newest-first audit events, optionally restricted to one event
    /// type and/or to events at or after <paramref name="since"/>, at
    /// most <paramref name="limit"/> records.
    /// </summary>
    Task<IReadOnlyList<AuthAuditEvent>> ListAsync(
        AuthAuditEventType? eventType = null,
        DateTimeOffset? since = null,
        int limit = 100,
        CancellationToken ct = default);
}
