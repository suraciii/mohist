namespace Mohist.Server.Auth.Domain;

/// <summary>
/// The closed set of auth events with a persistent audit record:
/// credential issuance / revocation,
/// enrollment-token issuance / consumption, device-authorization
/// approval and session establishment.
/// </summary>
public enum AuthAuditEventType
{
    CredentialIssued,
    CredentialRevoked,
    EnrollmentTokenIssued,
    EnrollmentTokenConsumed,
    DeviceApproved,
    SessionEstablished,
}
