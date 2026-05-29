using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mohist.Server.Workflow.Domain.Run;

public record TaskRun(
    string DefinitionId,
    int Attempt,
    string Title,
    string? Uses,
    Dictionary<string, JsonElement?>? WithInput,
    TaskRunPhase Phase)
{
    [JsonIgnore]
    public string Id => $"{DefinitionId}.{Attempt}";
}
