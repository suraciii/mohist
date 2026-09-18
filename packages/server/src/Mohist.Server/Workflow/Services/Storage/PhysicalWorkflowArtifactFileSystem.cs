namespace Mohist.Server.Workflow.Storage;

/// <summary>
/// Host-filesystem implementation of <see cref="IWorkflowArtifactFileSystem"/>.
/// </summary>
internal sealed class PhysicalWorkflowArtifactFileSystem : IWorkflowArtifactFileSystem
{
    public static readonly PhysicalWorkflowArtifactFileSystem Instance = new();

    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void DeleteFile(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    public void DeleteDirectory(string path, bool recursive) => Directory.Delete(path, recursive);

    public void MoveFile(string source, string destination) => File.Move(source, destination);

    public Stream OpenRead(string path) =>
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    public Stream OpenWrite(string path) =>
        new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true);

    public Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken) =>
        File.ReadAllTextAsync(path, cancellationToken);
}
