using System.Text.Json;
using Mohist.Server.Infrastructure;

namespace Mohist.Server.Workflow.Storage;

/// <summary>
/// Builds a <see cref="WorkflowArtifactDirectoryListing"/> from the durable
/// entry manifest recorded in an artifact's metadata. Both the filesystem
/// and in-memory storages read directory listings through this factory so
/// the read contract cannot drift and the validation is hermetically
/// testable.
/// </summary>
/// <remarks>
/// The listing is never derived from the current contents of the collection
/// directory. Missing, malformed, or structurally inconsistent metadata is a
/// rejected artifact, not an empty listing.
/// </remarks>
public static class WorkflowArtifactDirectoryListingFactory
{
    public const string DirectoryKind = "directory";

    /// <summary>
    /// Parses recorded metadata JSON. A malformed payload is a rejected
    /// artifact, not an unhandled deserialization failure. Missing content
    /// yields <c>null</c> so the caller can distinguish it from rejection.
    /// </summary>
    public static WorkflowArtifactStorageMetadata? ParseMetadata(string storagePath, string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<WorkflowArtifactStorageMetadata>(json, JSON.Indented);
        }
        catch (JsonException ex)
        {
            throw new WorkflowArtifactStorageException(
                $"Artifact metadata for '{storagePath}' is malformed JSON.", ex);
        }
    }

    /// <summary>
    /// Validates a recorded directory manifest and returns the recorded
    /// listing sorted by <see cref="WorkflowArtifactDirectoryEntry.RelativePath"/>.
    /// </summary>
    public static WorkflowArtifactDirectoryListing FromMetadata(
        string storagePath,
        WorkflowArtifactStorageMetadata? metadata)
    {
        if (metadata is null)
            throw new WorkflowArtifactStorageException(
                $"Artifact metadata for '{storagePath}' is missing; refusing to derive a directory listing from stored files.");

        if (!string.Equals(metadata.Kind, DirectoryKind, StringComparison.Ordinal))
            throw new WorkflowArtifactStorageException(
                $"Artifact '{storagePath}' is recorded as kind '{metadata.Kind}', not '{DirectoryKind}'.");

        if (metadata.Entries is null)
            throw new WorkflowArtifactStorageException(
                $"Directory artifact '{storagePath}' has no recorded entry manifest.");

        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<WorkflowArtifactDirectoryEntry>(metadata.Entries.Count);
        long totalSize = 0;
        for (var index = 0; index < metadata.Entries.Count; index++)
        {
            var entry = metadata.Entries[index];
            if (entry is null)
                throw new WorkflowArtifactStorageException(
                    $"Directory artifact '{storagePath}' has a null entry at index {index}.");

            var relativePath = WorkflowArtifactContainedPath.Parse(entry.RelativePath).Value;
            if (!seenPaths.Add(relativePath))
                throw new WorkflowArtifactStorageException(
                    $"Directory artifact '{storagePath}' lists '{relativePath}' more than once.");

            if (entry.Size < 0)
                throw new WorkflowArtifactStorageException(
                    $"Directory artifact '{storagePath}' entry '{relativePath}' has a negative recorded size ({entry.Size}).");

            if (string.IsNullOrWhiteSpace(entry.ContentHash))
                throw new WorkflowArtifactStorageException(
                    $"Directory artifact '{storagePath}' entry '{relativePath}' has no recorded content hash.");

            totalSize += entry.Size;
            entries.Add(new WorkflowArtifactDirectoryEntry
            {
                RelativePath = relativePath,
                Size = entry.Size,
                ContentHash = entry.ContentHash,
                ContentType = entry.ContentType,
            });
        }

        entries.Sort((a, b) => StringComparer.Ordinal.Compare(a.RelativePath, b.RelativePath));
        return new WorkflowArtifactDirectoryListing(storagePath, entries, totalSize);
    }
}
