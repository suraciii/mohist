namespace Mohist.Server.Auth.Domain;

/// <summary>
/// Persistence contract for attribution anchors. Principals are not
/// credentials: admin and service principals are implied by the file
/// credential bootstrap, and agent principals are established when an
/// Agent definition is created. There is no create/delete API — the
/// store only ever ensures the row an Agent already implies, so
/// historical attribution keeps resolving after the Agent is archived.
/// </summary>
public interface IPrincipalStore
{
    /// <summary>
    /// Creates the agent principal with the given id and name unless it
    /// already exists. Idempotent; never revokes or mutates an existing
    /// row (an id collision is a no-op, not an error).
    /// </summary>
    Task EnsureAgentPrincipalAsync(string principalId, string name, CancellationToken ct = default);
}
