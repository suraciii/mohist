namespace Mohist.Server.Auth.Identity;

/// <summary>
/// The carrier the presented credential arrived on: the
/// <c>Authorization: Bearer</c> header or the <c>mohist_session</c>
/// cookie (the authentication boundary). The direct external
/// Agent API accepts the Bearer carrier only; the auth layer records the
/// carrier so that boundary can enforce it without re-resolving the
/// token.
/// </summary>
public enum CredentialCarrier
{
    Bearer,
    Cookie,
}

/// <summary>
/// The <see cref="HttpContext.Items"/> key the resolved carrier kind is
/// recorded under by the auth layer.
/// </summary>
public static class CredentialCarrierResolution
{
    public const string HttpContextItemKey = "mohist.credentialCarrier";
}
