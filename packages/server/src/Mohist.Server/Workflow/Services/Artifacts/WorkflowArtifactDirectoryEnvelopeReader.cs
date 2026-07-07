using System.Text.Json;
using Mohist.Server.Infrastructure;
using Mohist.Server.Workflow.Storage;

namespace Mohist.Server.Workflow.Services.Artifacts;

internal static class WorkflowArtifactDirectoryEnvelopeReader
{
    public const string ContentType = "application/x-mohist-artifact-directory";

    public static bool IsDirectoryContentType(string? contentType) =>
        !string.IsNullOrWhiteSpace(contentType)
        && string.Equals(contentType, ContentType, StringComparison.OrdinalIgnoreCase);

    public static async Task<WorkflowArtifactDirectoryEnvelope> ReadAsync(
        Stream content,
        long declaredSize,
        CancellationToken cancellationToken)
    {
        byte[] bytes;
        await using (var ms = new MemoryStream())
        {
            await content.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
            bytes = ms.ToArray();
        }

        if (declaredSize >= 0 && bytes.LongLength != declaredSize)
        {
            throw new InvalidDataException(
                $"Directory envelope size mismatch: declared {declaredSize} bytes, read {bytes.LongLength} bytes.");
        }

        DirectoryEnvelopeDto? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<DirectoryEnvelopeDto>(bytes, JSON.Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"Directory upload content is not a valid artifact envelope: {ex.Message}", ex);
        }

        if (envelope is null || !string.Equals(envelope.Kind, "directory", StringComparison.Ordinal))
            throw new InvalidDataException("Directory upload envelope must declare kind: \"directory\".");
        if (envelope.Files is null || envelope.Files.Count == 0)
            throw new InvalidDataException("Directory upload envelope must contain at least one contained file.");

        var entries = new List<WorkflowArtifactDirectoryEntryInput>(envelope.Files.Count);
        foreach (var file in envelope.Files)
        {
            if (file is null) continue;
            if (string.IsNullOrWhiteSpace(file.Path))
                throw new InvalidDataException("Directory entry path is required.");

            byte[] data;
            try
            {
                data = Convert.FromBase64String(file.Data ?? string.Empty);
            }
            catch (FormatException ex)
            {
                throw new InvalidDataException(
                    $"Directory entry '{file.Path}' data is not valid base64: {ex.Message}", ex);
            }

            entries.Add(new WorkflowArtifactDirectoryEntryInput
            {
                RelativePath = file.Path,
                Size = file.Size ?? data.LongLength,
                ContentType = file.ContentType,
                OpenContent = () => new MemoryStream(data, writable: false),
            });
        }

        return new WorkflowArtifactDirectoryEnvelope(entries);
    }

    private sealed class DirectoryEnvelopeDto
    {
        public string? Kind { get; set; }
        public List<DirectoryEnvelopeFileDto>? Files { get; set; }
    }

    private sealed class DirectoryEnvelopeFileDto
    {
        public string? Path { get; set; }
        public long? Size { get; set; }
        public string? ContentType { get; set; }
        public string? Data { get; set; }
    }
}

internal sealed record WorkflowArtifactDirectoryEnvelope(
    IReadOnlyList<WorkflowArtifactDirectoryEntryInput> Entries);
