using System.Text;

namespace Mohist.Cli;

internal interface IFileSystem
{
    string CurrentDirectory { get; }
    bool Exists(string path);
    bool DirectoryExists(string path);
    void CreateDirectory(string path);
    void Delete(string path);
    void DeleteDirectory(string path);
    void DeleteDirectoryForCleanup(string path) => DeleteDirectory(path);
    void Move(string source, string destination);
    void MoveFile(string source, string destination);
    void CopyFile(string source, string destination)
    {
        using var input = OpenRead(source);
        using var output = OpenWrite(destination);
        input.CopyTo(output);
    }
    string ReadAllText(string path);
    Task<string> ReadAllTextAsync(string path);
    void WriteAllText(string path, string contents);
    Task WriteAllTextAsync(string path, string contents);

    /// <summary>
    /// Writes the file restricted to the owning user (0600 on Unix; the
    /// user-profile directory already restricts on Windows). Used for
    /// local session credentials whose exposure equals the session's.
    /// </summary>
    void WriteAllTextUserOnly(string path, string contents) => WriteAllText(path, contents);
    IEnumerable<string> EnumerateFiles(string path, string searchPattern, SearchOption searchOption);
    IEnumerable<string> EnumerateDirectories(string path, SearchOption searchOption) => [];
    Stream OpenRead(string path);
    Stream OpenWrite(string path);
    IDisposable? TryAcquireExclusiveLock(string path) => new NoopFileSystemLock();
    bool IsSymbolicLink(string path) => false;
    bool IsUserOnlyFile(string path) => true;
}

internal sealed class RealFileSystem : IFileSystem
{
    public static readonly RealFileSystem Instance = new();

    private RealFileSystem()
    {
    }

    public string CurrentDirectory => Directory.GetCurrentDirectory();

    public bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void Delete(string path) => File.Delete(path);

    public void DeleteDirectory(string path) => Directory.Delete(path, recursive: true);

    public void DeleteDirectoryForCleanup(string path)
    {
        if (!Directory.Exists(path))
            return;

        MakeOwnerWritable(path);
        Directory.Delete(path, recursive: true);
    }

    public void Move(string source, string destination) => Directory.Move(source, destination);

    public void MoveFile(string source, string destination) => File.Move(source, destination, overwrite: true);

    public void CopyFile(string source, string destination)
    {
        File.Copy(source, destination, overwrite: true);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(destination, File.GetUnixFileMode(source));
    }

    public string ReadAllText(string path) => File.ReadAllText(path);

    public Task<string> ReadAllTextAsync(string path) => File.ReadAllTextAsync(path, Encoding.UTF8);

    public void WriteAllText(string path, string contents) => File.WriteAllText(path, contents, new UTF8Encoding(false));

    public void WriteAllTextUserOnly(string path, string contents)
    {
        File.WriteAllText(path, contents, new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public async Task WriteAllTextAsync(string path, string contents)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(path, contents, new UTF8Encoding(false));
    }

    public IEnumerable<string> EnumerateFiles(string path, string searchPattern, SearchOption searchOption) =>
        Directory.EnumerateFiles(path, searchPattern, searchOption);

    public IEnumerable<string> EnumerateDirectories(string path, SearchOption searchOption) =>
        Directory.EnumerateDirectories(path, "*", searchOption);

    public Stream OpenRead(string path) => File.OpenRead(path);

    public Stream OpenWrite(string path) => File.Create(path);

    public IDisposable? TryAcquireExclusiveLock(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public bool IsSymbolicLink(string path) => File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);

    public bool IsUserOnlyFile(string path)
    {
        if (OperatingSystem.IsWindows()) return true;
        var mode = File.GetUnixFileMode(path);
        var groupOrOther = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        return (mode & groupOrOther) == 0;
    }

    private static void MakeOwnerWritable(string root)
    {
        foreach (var directory in EnumerateDirectoriesWithoutLinks(root))
            MakeOwnerWritableAttributes(directory, directory: true);
        foreach (var file in EnumerateFilesWithoutLinks(root))
            MakeOwnerWritableAttributes(file, directory: false);
        MakeOwnerWritableAttributes(root, directory: true);
    }

    private static IEnumerable<string> EnumerateDirectoriesWithoutLinks(string root)
    {
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            if (IsReparsePoint(directory))
                continue;
            yield return directory;
            foreach (var nested in EnumerateDirectoriesWithoutLinks(directory))
                yield return nested;
        }
    }

    private static IEnumerable<string> EnumerateFilesWithoutLinks(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root))
        {
            if (!IsReparsePoint(file))
                yield return file;
        }

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            if (!IsReparsePoint(directory))
            {
                foreach (var nested in EnumerateFilesWithoutLinks(directory))
                    yield return nested;
            }
        }
    }

    private static bool IsReparsePoint(string path) =>
        File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);

    private static void MakeOwnerWritableAttributes(string path, bool directory)
    {
        if (OperatingSystem.IsWindows())
        {
            var attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.ReadOnly))
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            return;
        }

        var mode = File.GetUnixFileMode(path);
        var writable = UnixFileMode.UserWrite | (directory ? UnixFileMode.UserExecute : UnixFileMode.None);
        File.SetUnixFileMode(path, mode | writable);
    }
}

internal sealed class NoopFileSystemLock : IDisposable
{
    public void Dispose()
    {
    }
}
