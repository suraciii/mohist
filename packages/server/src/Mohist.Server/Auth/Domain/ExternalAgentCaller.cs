namespace Mohist.Server.Auth.Domain;

/// <summary>
/// The runtime caller identity for the direct external Agent API
/// (the ExternalAgentCaller contract): resolved from the persisted
/// <see cref="Credential"/> behind the presented Bearer PAT — never from
/// a caller-supplied value. <see cref="CallerKeyId"/> is the Credential
/// ID, stable across retries; <see cref="ProjectGrant"/> is the
/// credential-owned direct API grant. A cookie session, a file
/// credential, or a trusted Agent Connection identity never resolves to
/// a caller.
/// </summary>
public sealed record ExternalAgentCaller(
    string CallerKeyId,
    string PrincipalId,
    IReadOnlyList<Scope> Scopes,
    DirectApiProjectGrant? ProjectGrant)
{
    /// <summary>
    /// The <see cref="HttpContext.Items"/> key the resolved caller is
    /// recorded under by the auth layer.
    /// </summary>
    public const string HttpContextItemKey = "mohist.externalAgentCaller";

    /// <summary>
    /// True when the PAT carries a persisted, structurally usable direct
    /// API grant. A missing, empty, or malformed grant disables the
    /// entire direct API for the credential.
    /// </summary>
    public bool IsDirectApiEnabled => ProjectGrant is { IsValid: true };

    /// <summary>
    /// Whether the grant authorizes the canonical Project ID.
    /// <see cref="DirectApiProjectGrantKind.OperatorAll"/> covers every
    /// Project; <see cref="DirectApiProjectGrantKind.Explicit"/> covers
    /// exactly the persisted list. An operator scope alone never
    /// authorizes a Project: <c>operator_all</c> is honored only as an
    /// explicitly persisted grant kind.
    /// </summary>
    public bool AuthorizesProject(string projectId) => ProjectGrant is { IsValid: true } grant
        && grant.Kind switch
        {
            DirectApiProjectGrantKind.OperatorAll => true,
            DirectApiProjectGrantKind.Explicit => grant.AllowedProjectIds.Contains(projectId),
            _ => false,
        };
}
