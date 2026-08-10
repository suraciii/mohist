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
    void Move(string source, string destination);
    void MoveFile(string source, string destination);
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
    Stream OpenRead(string path);
    Stream OpenWrite(string path);
    bool IsSymbolicLink(string path) => false;
    bool IsUserOnlyFile(string path) => true;

    /// <summary>
    /// Replaces a managed directory link. The target is always an already-built,
    /// immutable runtime version; callers never use it to point at source trees.
    /// </summary>
    void ReplaceDirectorySymbolicLink(string linkPath, string targetPath) =>
        throw new PlatformNotSupportedException("Directory symbolic links are not available from this filesystem.");

    string? ReadDirectorySymbolicLink(string linkPath) => null;

    void DeleteDirectorySymbolicLink(string linkPath) => Delete(linkPath);
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

    public void Move(string source, string destination) => Directory.Move(source, destination);

    public void MoveFile(string source, string destination) => File.Move(source, destination, overwrite: true);

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

    public Stream OpenRead(string path) => File.OpenRead(path);

    public Stream OpenWrite(string path) => File.Create(path);

    public bool IsSymbolicLink(string path) => File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);

    public void ReplaceDirectorySymbolicLink(string linkPath, string targetPath)
    {
        var parent = Path.GetDirectoryName(linkPath);
        if (!string.IsNullOrWhiteSpace(parent))
            Directory.CreateDirectory(parent);

        var temporaryLink = linkPath + ".next";
        DeleteDirectorySymbolicLink(temporaryLink);
        Directory.CreateSymbolicLink(temporaryLink, targetPath);
        File.Move(temporaryLink, linkPath, overwrite: true);
    }

    public string? ReadDirectorySymbolicLink(string linkPath)
    {
        var directory = new DirectoryInfo(linkPath);
        var target = directory.LinkTarget;
        if (string.IsNullOrWhiteSpace(target))
            return null;
        return Path.IsPathRooted(target)
            ? target
            : Path.GetFullPath(target, directory.Parent?.FullName ?? Directory.GetCurrentDirectory());
    }

    public void DeleteDirectorySymbolicLink(string linkPath)
    {
        var directory = new DirectoryInfo(linkPath);
        if (!string.IsNullOrWhiteSpace(directory.LinkTarget))
        {
            directory.Delete();
            return;
        }

        if (File.Exists(linkPath) && new FileInfo(linkPath).LinkTarget is not null)
            File.Delete(linkPath);
    }

    public bool IsUserOnlyFile(string path)
    {
        if (OperatingSystem.IsWindows()) return true;
        var mode = File.GetUnixFileMode(path);
        var groupOrOther = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        return (mode & groupOrOther) == 0;
    }
}
