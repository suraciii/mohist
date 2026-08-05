namespace Mohist.Server.Logging;

/// <summary>
/// Resolves the directory under which the server writes its structured
/// log file. The same value is the source of truth for
/// <c>SystemInfoService</c>'s advertised <c>Logs</c> path and for the
/// <c>/api/logs/tail</c> reader, so the file logger, the tail endpoint,
/// and the system info response cannot drift.
/// </summary>
public interface ILogPathResolver
{
    /// <summary>
    /// Absolute path to the directory the file logger writes to and the
    /// tail reader reads from. Always non-null and non-empty.
    /// </summary>
    string Resolve();
}
