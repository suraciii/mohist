namespace Mohist.Server.Infrastructure;

public enum TaskLogAppendResult
{
    Changed,
    Duplicate,
    NotFound,
    Conflict,
}

/// <summary>
/// One captured line of an ops task's execution log in
/// transport-friendly form. Lives in the root Infrastructure
/// namespace so both <c>Infrastructure.Data.Runner.TaskLogStore</c>
/// and <c>Runner.Services.TaskLogService</c> (and the API layer via
/// the service) can share it without forming an application/data
/// dependency cycle (architectural rule:
/// Infrastructure.Data does not depend on Application).
/// </summary>
public sealed record TaskLogLine(
    long Seq,
    DateTimeOffset Timestamp,
    string Source,
    string Text);

/// <summary>
/// One page of a cursor-paginated query. <see cref="NextCursor"/>
/// is the seq to send as the next request's <c>cursor</c>; it is
/// <c>null</c> on the final page. <see cref="Truncated"/> reports
/// whether the runner dropped head lines at capture time.
/// </summary>
public sealed record TaskLogPage(
    IReadOnlyList<TaskLogLine> Lines,
    long? NextCursor,
    bool Truncated);

public static class TaskLogUploadLimits
{
    public const int MaxEntries = 20_000;
    public const int MaxSourceLength = 64;
    public const int MaxTextLength = 32_768;
    public const int MaxTotalTextLength = 1_000_000;
}
