using System.Text.Json;

namespace Mohist.Runner;

public interface IWorkspaceManager
{
    Task<WorkspaceInfo> EnsureAsync(Dictionary<string, JsonElement?> variables, CancellationToken ct);
}

public sealed record WorkspaceInfo(
    string Path,
    string? Branch,
    string? ChangeDir);
