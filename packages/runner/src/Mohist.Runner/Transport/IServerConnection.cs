using System.Text.Json;

namespace Mohist.Runner.Transport;

public interface IServerConnection
{
    Task ConnectAsync(CancellationToken ct);
    Task HeartbeatAsync(CancellationToken ct);
    Task DisconnectAsync(CancellationToken ct);
    Task<WorkItem?> PollAsync(CancellationToken ct);
    Task ReportAsync(WorkItem workItem, WorkItemResult result, CancellationToken ct);
}

public record WorkItem(
    string WorkflowRunId,
    string WorkId,
    string WorkType,
    string? Stage,
    string? Title,
    string? Uses,
    Dictionary<string, JsonElement?>? With);

public record WorkItemResult(
    string Status,
    string? Message = null,
    string? Output = null,
    int? ExitCode = null);
