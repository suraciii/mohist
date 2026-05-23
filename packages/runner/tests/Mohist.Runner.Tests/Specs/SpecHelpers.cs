using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Runner.Actions;
using Mohist.Runner.Transport;

namespace Mohist.Runner.Tests.Specs;

internal static class SpecHelpers
{
    public static ActionContext Context(string workDir, string type, string uses, object? with = null)
    {
        var input = with is null
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, JsonElement?>>(JsonSerializer.Serialize(with));

        return new ActionContext("wr-1", "work-1", type, "build", "Work", uses, input, workDir, CancellationToken.None);
    }

    public static WorkItem Work(string type, string uses = "spec/action", object? with = null)
    {
        var input = with is null
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, JsonElement?>>(JsonSerializer.Serialize(with));

        return new WorkItem("wr-1", $"{type}-1", type, "build", "Work", uses, input);
    }

    public static ILogger<T> Logger<T>() => NullLogger<T>.Instance;
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
