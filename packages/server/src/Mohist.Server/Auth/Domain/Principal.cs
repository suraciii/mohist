namespace Mohist.Server.Auth.Domain;

public sealed record Principal(string Id, PrincipalKind Kind, string Name, DateTimeOffset CreatedAt);
