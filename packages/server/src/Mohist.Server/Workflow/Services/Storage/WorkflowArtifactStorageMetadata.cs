using System.Text.Json.Serialization;

namespace Mohist.Server.Workflow.Storage;

/// <summary>
/// Metadata persisted alongside every recorded artifact in
/// <c>metadata.json</c>. Captures the durable business identity
/// (<see cref="WorkflowRunId"/>, <see cref="ActionAttemptId"/>, source
/// <see cref="Path"/>, <see cref="RecordedAt"/>) plus the transport
/// fields (size, content type, content hash, kind). The original source
/// path is preserved verbatim for display and history queries; it is
/// never used as a filesystem path segment.
/// </summary>
public sealed class WorkflowArtifactStorageMetadata
{
    [JsonPropertyName("workflowRunId")]
    public string WorkflowRunId { get; set; } = string.Empty;

    [JsonPropertyName("actionAttemptId")]
    public string ActionAttemptId { get; set; } = string.Empty;

    [JsonPropertyName("artifactId")]
    public string ArtifactId { get; set; } = string.Empty;

    /// <summary>
    /// Original artifact source path. Stored verbatim, including
    /// unusual separators and characters, for display and history
    /// queries. Not used to derive any filesystem path.
    /// </summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "file";

    [JsonPropertyName("recordedAt")]
    public DateTimeOffset RecordedAt { get; set; }

    [JsonPropertyName("contentType")]
    public string? ContentType { get; set; }

    [JsonPropertyName("contentHash")]
    public string? ContentHash { get; set; }

    [JsonPropertyName("size")]
    public long? Size { get; set; }

    /// <summary>
    /// File count for directory artifacts; <c>null</c> for file
    /// artifacts. Captured at write time so consumers can reason
    /// about a directory without re-enumerating the tree.
    /// </summary>
    [JsonPropertyName("fileCount")]
    public int? FileCount { get; set; }
}
