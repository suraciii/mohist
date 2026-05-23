using System.Text.Json;

namespace Mohist.Server.Variables.Grains;

public class VariableScopeGrain : Grain, IVariableScopeGrain
{
    private readonly Dictionary<string, string> _contexts = new(StringComparer.OrdinalIgnoreCase);

    public Task SetContextAsync(string name, string json)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Context name is required", nameof(name));

        using var _ = JsonDocument.Parse(json);
        _contexts[name] = json;
        return Task.CompletedTask;
    }

    public Task<string> SnapshotAsync(VariableSnapshotRequest request)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();

            foreach (var (name, json) in _contexts)
            {
                writer.WritePropertyName(name);
                using var document = JsonDocument.Parse(json);
                document.RootElement.WriteTo(writer);
            }

            WriteDispatchContext(writer, request);
            writer.WriteEndObject();
        }

        return Task.FromResult(System.Text.Encoding.UTF8.GetString(stream.ToArray()));
    }

    public Task ClearAsync()
    {
        _contexts.Clear();
        return Task.CompletedTask;
    }

    private static void WriteDispatchContext(Utf8JsonWriter writer, VariableSnapshotRequest request)
    {
        writer.WritePropertyName("workflow");
        writer.WriteStartObject();
        writer.WriteString("runId", request.WorkflowRunId);
        writer.WriteEndObject();

        writer.WritePropertyName("stage");
        writer.WriteStartObject();
        writer.WriteString("name", request.Stage);
        writer.WriteEndObject();

        writer.WritePropertyName("work");
        writer.WriteStartObject();
        writer.WriteString("id", request.WorkId);
        writer.WriteString("type", request.WorkType);
        writer.WriteString("title", request.Title);
        writer.WriteEndObject();
    }
}
