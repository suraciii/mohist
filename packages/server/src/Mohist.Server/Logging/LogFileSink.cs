using System.Text;

namespace Mohist.Server.Logging;

internal interface ILogFileSink : IDisposable
{
    void WriteLine(string line);
    void Flush();
}

internal interface ILogFileSinkFactory
{
    ILogFileSink Open(string path);
}

internal sealed class FileSystemLogFileSinkFactory : ILogFileSinkFactory
{
    public static readonly FileSystemLogFileSinkFactory Instance = new();

    private FileSystemLogFileSinkFactory()
    {
    }

    public ILogFileSink Open(string path) => new FileSystemLogFileSink(path);
}

internal sealed class FileSystemLogFileSink : ILogFileSink
{
    private readonly StreamWriter _writer;

    public FileSystemLogFileSink(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var stream = new FileStream(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            options: FileOptions.WriteThrough);
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public void WriteLine(string line) => _writer.WriteLine(line);

    public void Flush() => _writer.Flush();

    public void Dispose() => _writer.Dispose();
}
