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
    void CopyFile(string source, string destination)
    {
        using var input = OpenRead(source);
        using var output = OpenWrite(destination);
        input.CopyTo(output);
    }
    void CopyFileDurable(string source, string destination) => CopyFile(source, destination);
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
    Stream? TryAcquireFileLock(string path) => new MemoryStream();
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

    public void Move(string source, string destination) => Directory.Move(source, destination);

    public void MoveFile(string source, string destination) => File.Move(source, destination, overwrite: true);

    public void CopyFile(string source, string destination)
    {
        File.Copy(source, destination, overwrite: true);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(destination, File.GetUnixFileMode(source));
    }

    public void CopyFileDurable(string source, string destination)
    {
        using var input = File.OpenRead(source);
        using var output = new FileStream(
            destination,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            FileOptions.WriteThrough);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(destination, File.GetUnixFileMode(source));
        input.CopyTo(output);
        output.Flush(flushToDisk: true);
    }

    public string ReadAllText(string path) => File.ReadAllText(path);

    public Task<string> ReadAllTextAsync(string path) => File.ReadAllTextAsync(path, Encoding.UTF8);

    public void WriteAllText(string path, string contents) => File.WriteAllText(path, contents, new UTF8Encoding(false));

    public void WriteAllTextUserOnly(string path, string contents)
    {
        using var stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var bytes = new UTF8Encoding(false).GetBytes(contents);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
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

    public Stream? TryAcquireFileLock(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        try
        {
            return new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);
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
        if (OperatingSystem.IsWindows())
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(profile)) return false;
            var fullPath = Path.GetFullPath(path);
            var fullProfile = Path.GetFullPath(profile).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return fullPath.StartsWith(fullProfile + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        var mode = File.GetUnixFileMode(path);
        var groupOrOther = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        return (mode & groupOrOther) == 0;
    }
}
