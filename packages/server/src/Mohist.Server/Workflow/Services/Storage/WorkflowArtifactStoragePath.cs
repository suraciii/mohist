namespace Mohist.Server.Workflow.Storage;

public static class WorkflowArtifactStorageLayout
{
    public const string MetadataFileName = "metadata.json";
    public const string FileContentName = "content";
    public const string DirectoryFilesName = "files";
    public const string StorageRootName = "artifacts";
    public const string WorkflowSegment = "workflows";
}

public readonly record struct WorkflowArtifactStoragePath
{
    private readonly string? _value;

    private WorkflowArtifactStoragePath(string value)
    {
        _value = value;
    }

    public string Value => _value ?? string.Empty;

    public bool IsFileContent => Value.EndsWith("/" + WorkflowArtifactStorageLayout.FileContentName, StringComparison.Ordinal);

    public bool IsDirectoryFiles => Value.EndsWith("/" + WorkflowArtifactStorageLayout.DirectoryFilesName, StringComparison.Ordinal);

    public static WorkflowArtifactStoragePath ForArtifact(
        string workflowRunId,
        string actionAttemptId,
        string artifactId,
        WorkflowArtifactStorageKind kind)
    {
        ValidateId(workflowRunId, nameof(workflowRunId));
        ValidateId(actionAttemptId, nameof(actionAttemptId));
        ValidateId(artifactId, nameof(artifactId));

        var relative = string.Join('/', new[]
        {
            WorkflowArtifactStorageLayout.WorkflowSegment,
            workflowRunId,
            "tasks",
            actionAttemptId,
            "artifacts",
            artifactId,
        });

        return new WorkflowArtifactStoragePath(kind == WorkflowArtifactStorageKind.Directory
            ? relative + "/" + WorkflowArtifactStorageLayout.DirectoryFilesName
            : relative + "/" + WorkflowArtifactStorageLayout.FileContentName);
    }

    public static WorkflowArtifactStoragePath Parse(string storagePath)
    {
        if (string.IsNullOrWhiteSpace(storagePath))
            throw new WorkflowArtifactStorageException("Storage path must be provided.");

        var trimmed = storagePath.Replace('\\', '/');
        while (trimmed.StartsWith("/"))
            trimmed = trimmed[1..];

        if (trimmed.Contains("..", StringComparison.Ordinal))
            throw new WorkflowArtifactStorageException(
                $"Storage path '{storagePath}' contains a traversal segment.");
        if (trimmed.Contains("\0", StringComparison.Ordinal))
            throw new WorkflowArtifactStorageException(
                $"Storage path '{storagePath}' contains a NUL character.");

        return new WorkflowArtifactStoragePath(trimmed);
    }

    public WorkflowArtifactStorageIdentity? TryReadIdentity()
    {
        var segments = Value.Replace('\\', '/').Split('/');
        return segments.Length >= 6
            && segments[0] == WorkflowArtifactStorageLayout.WorkflowSegment
            && segments[2] == "tasks"
            && segments[4] == "artifacts"
            ? new WorkflowArtifactStorageIdentity(segments[1], segments[3], segments[5])
            : null;
    }

    public override string ToString() => Value;

    private static void ValidateId(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new WorkflowArtifactStorageException($"{paramName} must be provided.");

        foreach (var ch in value)
        {
            if (ch is '/' or '\\' or '\0' or ' ' or ':')
                throw new WorkflowArtifactStorageException(
                    $"{paramName} contains an unsafe character: '{ch}'.");
        }

        if (value == "." || value == "..")
            throw new WorkflowArtifactStorageException(
                $"{paramName} must not be a traversal segment.");
    }
}

public readonly record struct WorkflowArtifactContainedPath
{
    private readonly string? _value;

    private WorkflowArtifactContainedPath(string value)
    {
        _value = value;
    }

    public string Value => _value ?? string.Empty;

    public static WorkflowArtifactContainedPath Parse(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new WorkflowArtifactStorageException("Contained relative path must be provided.");

        var trimmed = relativePath.Replace('\\', '/');
        if (trimmed.StartsWith("/") || Path.IsPathRooted(relativePath))
            throw new WorkflowArtifactStorageException(
                $"Contained relative path '{relativePath}' must be relative to the collection root.");

        while (trimmed.StartsWith("/"))
            trimmed = trimmed[1..];

        if (trimmed.Length == 0)
            throw new WorkflowArtifactStorageException("Contained relative path must be non-empty.");
        if (trimmed.Contains("..", StringComparison.Ordinal))
            throw new WorkflowArtifactStorageException(
                $"Contained relative path '{relativePath}' contains a traversal segment.");
        if (trimmed.Contains("\0", StringComparison.Ordinal))
            throw new WorkflowArtifactStorageException(
                $"Contained relative path '{relativePath}' contains a NUL character.");

        foreach (var segment in trimmed.Split('/'))
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
                throw new WorkflowArtifactStorageException(
                    $"Contained relative path '{relativePath}' contains an invalid segment.");
        }

        return new WorkflowArtifactContainedPath(trimmed);
    }

    public override string ToString() => Value;
}

public sealed record WorkflowArtifactStorageIdentity(
    string WorkflowRunId,
    string ActionAttemptId,
    string ArtifactId);
