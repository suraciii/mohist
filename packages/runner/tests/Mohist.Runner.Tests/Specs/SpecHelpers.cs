using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Runner.Actions;
using Mohist.Runner.Transport;

namespace Mohist.Runner.Tests.Specs;

internal static class SpecHelpers
{
    public static ActionContext Context(string workDir, string type, string uses, object? with = null, object? variables = null, string? stage = "build")
    {
        var input = with is null
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, JsonElement?>>(JsonSerializer.Serialize(with));
        var vars = variables is null
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, JsonElement?>>(JsonSerializer.Serialize(variables));

        return new ActionContext("wr-1", "work-1", type, stage, "Work", uses, input, vars, workDir, CancellationToken.None);
    }

    public static WorkItem Work(string type, string uses = "spec/action", object? with = null, object? variables = null, string? stage = "build")
    {
        var input = with is null
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, JsonElement?>>(JsonSerializer.Serialize(with));
        var vars = variables is null
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, JsonElement?>>(JsonSerializer.Serialize(variables));

        return new WorkItem("wr-1", $"{type}-1", type, stage, "Work", uses, input, vars);
    }

    public static ILogger<T> Logger<T>() => NullLogger<T>.Instance;

    public static IWorkspaceManager CreateWorkspaceManager(string workspacePath)
    {
        return new FakeWorkspaceManager(workspacePath);
    }

    private sealed class FakeWorkspaceManager : IWorkspaceManager
    {
        private readonly string _fallbackPath;

        public FakeWorkspaceManager(string fallbackPath)
        {
            _fallbackPath = fallbackPath;
        }

        public Task<WorkspaceInfo> EnsureAsync(Dictionary<string, JsonElement?> variables, CancellationToken ct)
        {
            var path = ResolveExistingWorkspace(variables) ?? _fallbackPath;
            return Task.FromResult(new WorkspaceInfo(path, null, null));
        }

        private static string? ResolveExistingWorkspace(Dictionary<string, JsonElement?> variables)
        {
            if (variables.TryGetValue("workspace", out var ws) &&
                ws is not null &&
                ws.Value.TryGetProperty("path", out var pathProp) &&
                pathProp.ValueKind == JsonValueKind.String)
            {
                return pathProp.GetString();
            }
            return null;
        }
    }
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mohist-runner-test-{Guid.NewGuid():N}");

    public TempDir()
    {
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        if (Directory.Exists(Path))
            Directory.Delete(Path, recursive: true);
    }
}
