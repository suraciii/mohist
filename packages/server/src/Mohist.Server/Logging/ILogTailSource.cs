namespace Mohist.Server.Logging;

public interface ILogTailSource
{
    string ExpectedLocation { get; }

    LogTailSnapshot Open();
}

public sealed record LogTailSnapshot(
    bool Available,
    string? Source,
    string? UnavailableReason,
    Func<Stream>? OpenContent)
{
    public static LogTailSnapshot Unavailable(string reason) =>
        new(false, null, reason, null);

    public static LogTailSnapshot AvailableContent(string source, Func<Stream> openContent) =>
        new(true, source, null, openContent);
}

public sealed class FileLogTailSource : ILogTailSource
{
    private readonly string _logDirectory;
    private readonly string _expectedFile;

    public FileLogTailSource(ILogPathResolver pathResolver)
    {
        _logDirectory = pathResolver.Resolve();
        _expectedFile = Path.Combine(_logDirectory, FileLoggerProvider.LogFileName);
    }

    public string ExpectedLocation => _expectedFile;

    public LogTailSnapshot Open()
    {
        var activeFile = ResolveActiveFile();
        if (activeFile is null)
        {
            var reason = Directory.Exists(_logDirectory)
                ? $"Log file '{FileLoggerProvider.LogFileName}' is missing at {_expectedFile}."
                : $"Log directory does not exist at {_logDirectory}.";
            return LogTailSnapshot.Unavailable(reason);
        }

        return LogTailSnapshot.AvailableContent(
            Path.GetFileName(activeFile),
            () => new FileStream(activeFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
    }

    private string? ResolveActiveFile()
    {
        if (File.Exists(_expectedFile))
            return _expectedFile;
        if (!Directory.Exists(_logDirectory))
            return null;
        return Directory.GetFiles(_logDirectory, "*.log")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }
}
