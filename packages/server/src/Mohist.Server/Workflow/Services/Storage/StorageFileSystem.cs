namespace Mohist.Server.Workflow.Storage;

internal readonly record struct StorageFileEntry(string Path, long Length, bool IsReparsePoint);

internal interface IStorageFileSystem
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
    void CreateDirectory(string path);
    void DeleteFile(string path);
    void DeleteDirectory(string path);
    bool IsDirectoryEmpty(string path);
    Stream OpenRead(string path);
    Stream OpenWrite(string path, FileMode mode);
    void MoveFile(string source, string destination, bool overwrite);
    IEnumerable<StorageFileEntry> EnumerateFiles(string root);
    bool IsReparsePoint(string path);
}

internal sealed class PhysicalStorageFileSystem : IStorageFileSystem
{
    public static readonly PhysicalStorageFileSystem Instance = new();

    private PhysicalStorageFileSystem()
    {
    }

    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void DeleteFile(string path) => File.Delete(path);

    public void DeleteDirectory(string path) => Directory.Delete(path, recursive: true);

    public bool IsDirectoryEmpty(string path) => !Directory.EnumerateFileSystemEntries(path).Any();

    public Stream OpenRead(string path) => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    public Stream OpenWrite(string path, FileMode mode) => new FileStream(
        path,
        mode,
        FileAccess.Write,
        FileShare.None,
        bufferSize: 81920,
        useAsync: true);

    public void MoveFile(string source, string destination, bool overwrite) => File.Move(source, destination, overwrite);

    public IEnumerable<StorageFileEntry> EnumerateFiles(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path =>
            {
                var info = new FileInfo(path);
                return new StorageFileEntry(
                    path,
                    info.Length,
                    (info.Attributes & FileAttributes.ReparsePoint) != 0);
            });

    public bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}
