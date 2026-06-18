namespace Mohist.Server.Workflow.Storage;

/// <summary>
/// Configuration for <see cref="IAttachmentStorage"/>. The storage root
/// is resolved from <c>Mohist:AttachmentStorage:Root</c>, then
/// <c>MOHIST_ATTACHMENT_ROOT</c>, otherwise <c>~/.mohist/attachments</c>.
/// </summary>
public sealed class AttachmentStorageOptions
{
    public const string SectionName = "Mohist:AttachmentStorage";
    public const string RootEnvironmentVariable = "MOHIST_ATTACHMENT_ROOT";

    public const long DefaultMaxFileBytes = 25L * 1024 * 1024;
    public const int DefaultMaxCountPerOwner = 20;

    public string? Root { get; set; }

    public long MaxFileBytes { get; set; } = DefaultMaxFileBytes;

    public int MaxCountPerOwner { get; set; } = DefaultMaxCountPerOwner;
}
