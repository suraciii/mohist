namespace Mohist.Server.Infrastructure.Data.Auth;

/// <summary>
/// One row of the auth audit trail. Metadata is JSON of non-secret
/// context only; token values are never stored — the trail carries
/// identifiers and hashes, matching the Credential/EnrollmentToken
/// storage discipline.
/// </summary>
public class AuthAuditEventRow
{
    public string Id { get; set; } = string.Empty;
    public string SubjectId { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string TargetKind { get; set; } = string.Empty;
    public string TargetId { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }
    public string MetadataJson { get; set; } = "{}";
}
