namespace Mohist.Server.Runner.Grains;

public static class WorkReportStatus
{
    public static bool IsWork(string? status) =>
        Is(status, "completed")
        || Is(status, "failed")
        || Is(status, "timeout")
        || Is(status, "unknown");

    public static bool IsChecks(string? status) =>
        Is(status, "pass") || Is(status, "fail");

    public static bool IsWorkflowEnvelope(string? status) =>
        IsWork(status) || IsChecks(status);

    public static bool IsCompleted(string? status) => Is(status, "completed");

    public static bool IsUnknown(string? status) => Is(status, "unknown");

    private static bool Is(string? actual, string expected) =>
        string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
}
