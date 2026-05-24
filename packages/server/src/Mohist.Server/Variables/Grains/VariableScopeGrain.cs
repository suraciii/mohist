using System.Text.Json;
using Mohist.Server.Storage;

namespace Mohist.Server.Variables.Grains;

public class VariableScopeGrain : Grain, IVariableScopeGrain
{
    private readonly Dictionary<string, string> _contexts = new(StringComparer.OrdinalIgnoreCase);
    private readonly IStateStore<VariableScopeState> _store;

    public VariableScopeGrain(IStateStore<VariableScopeState> store)
    {
        _store = store;
    }

    private string GrainKey => this.GetPrimaryKeyString();

    public override async Task OnActivateAsync(CancellationToken ct)
    {
        var state = await _store.LoadAsync(GrainKey);
        if (state is null) return;
        foreach (var (name, json) in state.Contexts)
            _contexts[name] = json;
    }

    public async Task SetContextAsync(string name, string json)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Context name is required", nameof(name));

        using var _ = JsonDocument.Parse(json);
        _contexts[name] = json;
        await SaveAsync();
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

    public async Task ClearAsync()
    {
        _contexts.Clear();
        await SaveAsync();
    }

    private Task SaveAsync() => _store.SaveAsync(GrainKey, new VariableScopeState(new Dictionary<string, string>(_contexts, StringComparer.OrdinalIgnoreCase)));

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
