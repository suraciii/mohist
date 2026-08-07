using System.Text;
using Mohist.Server.Logging;

namespace Mohist.Server.TestSupport;

public sealed class InMemoryLogTailSource : ILogTailSource
{
    public const string SourceName = "server.log";

    private readonly object _gate = new();
    private byte[]? _content;
    private string? _unavailableReason;

    public string ExpectedLocation => "/mohist-tests/logs/server.log";

    public void ResetDirectoryMissing()
    {
        lock (_gate)
        {
            _content = null;
            _unavailableReason = "Log directory does not exist at /mohist-tests/logs.";
        }
    }

    public void ResetFileMissing()
    {
        lock (_gate)
        {
            _content = null;
            _unavailableReason = $"Log file '{SourceName}' is missing at {ExpectedLocation}.";
        }
    }

    public void SetLines(params string[] lines)
    {
        lock (_gate)
        {
            _content = Encoding.UTF8.GetBytes(string.Concat(lines.Select(line => line + "\n")));
            _unavailableReason = null;
        }
    }

    public void AppendLine(string line)
    {
        lock (_gate)
        {
            var appended = Encoding.UTF8.GetBytes(line + "\n");
            var current = _content ?? [];
            var content = new byte[current.Length + appended.Length];
            current.CopyTo(content, 0);
            appended.CopyTo(content, current.Length);
            _content = content;
            _unavailableReason = null;
        }
    }

    public LogTailSnapshot Open()
    {
        lock (_gate)
        {
            if (_content is null)
                return LogTailSnapshot.Unavailable(_unavailableReason ?? "Log source is unavailable.");
            var snapshot = _content.ToArray();
            return LogTailSnapshot.AvailableContent(
                SourceName,
                () => new MemoryStream(snapshot, writable: false));
        }
    }
}
