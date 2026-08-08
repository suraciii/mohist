namespace Mohist.Server.Auth.Domain;

/// <summary>
/// One audit record for the auth trail. The subject is the acting
/// Principal; the target is the credential, enrollment token or device
/// approval the event is about. Factories accept only identifiers and
/// non-secret context — full token values never reach this record, so
/// the no-plaintext invariant is structural, not a call-site habit.
/// </summary>
public sealed record AuthAuditEvent(
    string Id,
    string SubjectId,
    AuthAuditEventType EventType,
    string TargetKind,
    string TargetId,
    DateTimeOffset OccurredAt,
    IReadOnlyDictionary<string, string> Metadata)
{
    public const string CredentialTargetKind = "credential";
    public const string EnrollmentTokenTargetKind = "enrollmentToken";
    public const string DeviceCodeTargetKind = "deviceCode";

    public static AuthAuditEvent CredentialIssued(
        string subjectId,
        string credentialId,
        CredentialKind credentialKind,
        string? name,
        DateTimeOffset occurredAt) =>
        New(
            subjectId,
            AuthAuditEventType.CredentialIssued,
            CredentialTargetKind,
            credentialId,
            occurredAt,
            KindMetadata(credentialKind, name));

    public static AuthAuditEvent CredentialRevoked(
        string subjectId,
        string credentialId,
        CredentialKind credentialKind,
        string? name,
        DateTimeOffset occurredAt) =>
        New(
            subjectId,
            AuthAuditEventType.CredentialRevoked,
            CredentialTargetKind,
            credentialId,
            occurredAt,
            KindMetadata(credentialKind, name));

    public static AuthAuditEvent EnrollmentTokenIssued(
        string subjectId,
        string tokenHash,
        DateTimeOffset occurredAt) =>
        New(
            subjectId,
            AuthAuditEventType.EnrollmentTokenIssued,
            EnrollmentTokenTargetKind,
            tokenHash,
            occurredAt,
            new Dictionary<string, string>(StringComparer.Ordinal));

    public static AuthAuditEvent EnrollmentTokenConsumed(
        string subjectId,
        string tokenHash,
        string runnerId,
        DateTimeOffset occurredAt) =>
        New(
            subjectId,
            AuthAuditEventType.EnrollmentTokenConsumed,
            EnrollmentTokenTargetKind,
            tokenHash,
            occurredAt,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["runnerId"] = runnerId });

    public static AuthAuditEvent DeviceApproved(
        string subjectId,
        string deviceCodeId,
        DateTimeOffset occurredAt) =>
        New(
            subjectId,
            AuthAuditEventType.DeviceApproved,
            DeviceCodeTargetKind,
            deviceCodeId,
            occurredAt,
            new Dictionary<string, string>(StringComparer.Ordinal));

    public static AuthAuditEvent SessionEstablished(
        string subjectId,
        string credentialId,
        DateTimeOffset occurredAt) =>
        New(
            subjectId,
            AuthAuditEventType.SessionEstablished,
            CredentialTargetKind,
            credentialId,
            occurredAt,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["kind"] = CredentialKind.Session.ToString().ToLowerInvariant() });

    private static AuthAuditEvent New(
        string subjectId,
        AuthAuditEventType eventType,
        string targetKind,
        string targetId,
        DateTimeOffset occurredAt,
        IReadOnlyDictionary<string, string> metadata) =>
        new(
            $"audit_{Guid.NewGuid():N}",
            subjectId,
            eventType,
            targetKind,
            targetId,
            occurredAt,
            metadata);

    private static IReadOnlyDictionary<string, string> KindMetadata(CredentialKind credentialKind, string? name)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Lowercase kind matches the wire names the product uses
            // (pat, runner, refresh, integration, session).
            ["kind"] = credentialKind.ToString().ToLowerInvariant(),
        };
        if (!string.IsNullOrEmpty(name))
            metadata["name"] = name;
        return metadata;
    }
}
