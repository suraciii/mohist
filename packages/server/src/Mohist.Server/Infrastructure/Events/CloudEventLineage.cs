namespace Mohist.Server.Infrastructure.Events;

public readonly record struct CloudEventIssueContext(
    string ProjectId,
    int IssueNumber,
    int? EpicNumber);

public readonly record struct CloudEventEpicContext(
    string ProjectId,
    int EpicNumber);

public static class CloudEventLineage
{
    public static bool TryReadIssueContext(
        CloudEvent evt,
        out CloudEventIssueContext context)
        => TryReadIssueContext(evt.Extensions, out context);

    public static bool TryReadIssueContext<TData>(
        CloudEvent<TData> evt,
        out CloudEventIssueContext context)
        where TData : class
        => TryReadIssueContext(evt.Extensions, out context);

    public static bool TryReadIssueContext(
        IReadOnlyDictionary<string, string> extensions,
        out CloudEventIssueContext context)
    {
        context = default;
        if (!TryReadProjectId(extensions, out var projectId)
            || !TryReadPositiveNumber(extensions, EventCatalog.Lineage.Issue, out var issueNumber))
        {
            return false;
        }

        int? epicNumber = TryReadPositiveNumber(
            extensions,
            EventCatalog.Lineage.Epic,
            out var parsedEpicNumber)
                ? parsedEpicNumber
                : null;
        context = new CloudEventIssueContext(projectId, issueNumber, epicNumber);
        return true;
    }

    public static bool TryReadEpicContext(
        CloudEvent evt,
        out CloudEventEpicContext context)
        => TryReadEpicContext(evt.Extensions, out context);

    public static bool TryReadEpicContext<TData>(
        CloudEvent<TData> evt,
        out CloudEventEpicContext context)
        where TData : class
        => TryReadEpicContext(evt.Extensions, out context);

    public static bool TryReadEpicContext(
        IReadOnlyDictionary<string, string> extensions,
        out CloudEventEpicContext context)
    {
        context = default;
        if (!TryReadProjectId(extensions, out var projectId)
            || !TryReadPositiveNumber(extensions, EventCatalog.Lineage.Epic, out var epicNumber))
        {
            return false;
        }

        context = new CloudEventEpicContext(projectId, epicNumber);
        return true;
    }

    public static bool TryReadProjectId(
        IReadOnlyDictionary<string, string> extensions,
        out string projectId)
        => TryReadValue(extensions, EventCatalog.Lineage.ProjectId, out projectId);

    public static bool TryReadPositiveNumber(
        IReadOnlyDictionary<string, string> extensions,
        string key,
        out int number)
    {
        number = 0;
        return extensions.TryGetValue(key, out var value)
            && int.TryParse(value, out number)
            && number > 0;
    }

    public static string? ReadValue(
        IReadOnlyDictionary<string, string> extensions,
        string key) =>
        TryReadValue(extensions, key, out var value) ? value : null;

    private static bool TryReadValue(
        IReadOnlyDictionary<string, string> extensions,
        string key,
        out string value)
    {
        if (extensions.TryGetValue(key, out var candidate)
            && !string.IsNullOrWhiteSpace(candidate))
        {
            value = candidate;
            return true;
        }

        value = string.Empty;
        return false;
    }
}
