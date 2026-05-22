using System.Text.Json;

namespace Mohist.Runner.Transport;

public interface IServerConnection
{
    Task ConnectAsync(CancellationToken ct);
    Task<WorkItem?> PollAsync(CancellationToken ct);
    Task ReportAsync(string workId, WorkItemResult result, CancellationToken ct);
}

public record WorkItem(
    string RunId,
    string Stage,
    string WorkId,
    string WorkType,
    string? Uses,
    Dictionary<string, JsonElement?>? With);

public record WorkItemResult(
    string Status,
    string? Message = null,
    JsonElement? Output = null,
    int? ExitCode = null);
