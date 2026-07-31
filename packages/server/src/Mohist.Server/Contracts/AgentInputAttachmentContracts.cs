using Orleans;

namespace Mohist.Server.Contracts;

[GenerateSerializer]
public sealed record AgentSessionInputAttachmentDescriptor(
    [property: Id(0)] string Id,
    [property: Id(1)] string OriginalFileName,
    [property: Id(2)] string? ContentType,
    [property: Id(3)] long Size,
    [property: Id(4)] DateTimeOffset AcceptedAt,
    [property: Id(5)] string Source = "upload",
    [property: Id(6)] string Availability = "usable");

public enum AgentInputAttachmentRejectionReason
{
    NotFound,
    Expired,
    NotReadable,
    ExceedsSizeLimit,
    UnsupportedType,
    AlreadyBound,
}

[GenerateSerializer]
public sealed record AgentInputAttachmentAcceptance(
    [property: Id(0)] string Id,
    [property: Id(1)] AgentSessionInputAttachmentDescriptor? Descriptor,
    [property: Id(2)] AgentInputAttachmentRejectionReason? RejectionReason,
    [property: Id(3)] string? RejectionMessage)
{
    public bool IsAccepted => Descriptor is not null;
}

public sealed record AgentInputAttachmentAcceptanceBatch(
    IReadOnlyList<AgentInputAttachmentAcceptance> Results,
    int AcceptedCount);
