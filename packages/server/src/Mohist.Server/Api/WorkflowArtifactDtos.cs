namespace Mohist.Server.Api;

public sealed record WorkflowArtifactDto
{
    public string artifactId { get; set; } = string.Empty;
    public string workflowRunId { get; set; } = string.Empty;
    public string actionAttemptId { get; set; } = string.Empty;
    public string path { get; set; } = string.Empty;
    public string kind { get; set; } = "file";
    public string? contentType { get; set; }
    public long? size { get; set; }
    public string recordedAt { get; set; } = string.Empty;
    public string? displayName { get; set; }
}

public sealed record WorkflowArtifactDirectoryDto
{
    public string artifactId { get; set; } = string.Empty;
    public string path { get; set; } = string.Empty;
    public string? displayName { get; set; }
    public string kind { get; set; } = "directory";
    public string recordedAt { get; set; } = string.Empty;
    public List<WorkflowArtifactDirectoryEntryDto> entries { get; set; } = [];
    public long totalSize { get; set; }
}

public sealed record WorkflowArtifactDirectoryEntryDto
{
    public string relativePath { get; set; } = string.Empty;
    public long size { get; set; }
    public string? contentType { get; set; }
}
