namespace Mohist.Server.Logging;

public sealed record LogFileDescriptor(string Path, DateTimeOffset LastWriteTime);

public interface ILogFileStore
{
    bool DirectoryExists(string path);
    bool FileExists(string path);
    IEnumerable<LogFileDescriptor> EnumerateLogFiles(string directory);
    Stream OpenRead(string path);
}

public sealed class FileSystemLogFileStore : ILogFileStore
{
    public bool DirectoryExists(string path) => Directory.Exists(path);

    public bool FileExists(string path) => File.Exists(path);

    public IEnumerable<LogFileDescriptor> EnumerateLogFiles(string directory) =>
        Directory.GetFiles(directory, "*.log")
            .Select(path => new LogFileDescriptor(path, File.GetLastWriteTimeUtc(path)));

    public Stream OpenRead(string path) => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
}
