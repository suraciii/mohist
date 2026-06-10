namespace Mohist.Server.Sessions.Domain.Events;

/// <summary>
/// Domain event raised when a runner becomes disconnected (e.g. heartbeat timeout).
/// Payload is empty; the runnerId is conveyed via the CloudEvent's "runnerid"
/// extension. Type string: "com.mohist.runner.disconnected".
/// </summary>
public sealed record RunnerDisconnected;
