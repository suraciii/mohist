namespace Mohist.Server.Sessions.Domain;

using Mohist.Server.Contracts;

// Follow-up attachment-descriptor helpers split from AgentSession.Transitions
// to keep the main partial within the file-size ratchet.
public static partial class AgentSessionExtensions
{
    private static IReadOnlyList<AgentSessionInputAttachmentDescriptor>? NormalizeAttachmentDescriptors(
        IReadOnlyList<AgentSessionInputAttachmentDescriptor>? descriptors)
    {
        if (descriptors is null || descriptors.Count == 0) return null;
        var copy = new List<AgentSessionInputAttachmentDescriptor>(descriptors.Count);
        foreach (var descriptor in descriptors)
        {
            if (descriptor is null || string.IsNullOrWhiteSpace(descriptor.Id)) continue;
            copy.Add(descriptor);
        }
        return copy.Count == 0 ? null : copy;
    }

    private static bool AttachmentDescriptorsEquivalent(
        IReadOnlyList<AgentSessionInputAttachmentDescriptor>? left,
        IReadOnlyList<AgentSessionInputAttachmentDescriptor>? right)
    {
        var leftCount = left?.Count ?? 0;
        var rightCount = right?.Count ?? 0;
        if (leftCount != rightCount) return false;
        if (leftCount == 0) return true;
        for (var index = 0; index < leftCount; index++)
        {
            var a = left![index];
            var b = right![index];
            if (!string.Equals(a.Id, b.Id, StringComparison.Ordinal)) return false;
            if (!string.Equals(a.OriginalFileName, b.OriginalFileName, StringComparison.Ordinal)) return false;
            if (!string.Equals(a.ContentType, b.ContentType, StringComparison.Ordinal)) return false;
            if (a.Size != b.Size) return false;
        }
        return true;
    }
}
