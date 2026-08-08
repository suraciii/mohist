namespace Mohist.Server.Auth.Domain;

/// <summary>
/// Unified emit entry for the auth audit trail.
/// Endpoints record after the fact; a failed write is
/// logged and swallowed so the audit can never block or fail the
/// operation that produced the event.
/// </summary>
public interface IAuthAuditRecorder
{
    Task RecordAsync(AuthAuditEvent auditEvent, CancellationToken ct = default);
}
