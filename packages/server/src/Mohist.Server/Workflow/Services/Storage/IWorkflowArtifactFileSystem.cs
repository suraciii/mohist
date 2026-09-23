namespace Mohist.Server.Workflow.Storage;

/// <summary>
/// Byte and directory operations used by
/// <see cref="FileSystemWorkflowArtifactStorage"/>. Production resolves
/// <see cref="PhysicalWorkflowArtifactFileSystem"/>. Specs inject an in-memory
/// implementation so the adapter's manifest, hashing, ordering, and cleanup
/// behavior is proven without touching a host filesystem.
/// </summary>
internal interface IWorkflowArtifactFileSystem
{
    bool FileExists(string path);

    bool DirectoryExists(string path);

    void CreateDirectory(string path);

    void DeleteFile(string path);

    void DeleteDirectory(string path, bool recursive);

    void MoveFile(string source, string destination);

    Stream OpenRead(string path);

    Stream OpenWrite(string path);

    Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken);
}
