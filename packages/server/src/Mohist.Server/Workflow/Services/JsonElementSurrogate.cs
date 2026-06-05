using System.Text.Json;
using Orleans.Serialization;

namespace Mohist.Server.Workflow.Services;

[GenerateSerializer]
public struct JsonElementSurrogate
{
    [Id(0)] public string RawJson;
}

[RegisterConverter]
public sealed class JsonElementSurrogateConverter : IConverter<JsonElement, JsonElementSurrogate>
{
    public JsonElement ConvertFromSurrogate(in JsonElementSurrogate surrogate) =>
        JsonSerializer.Deserialize<JsonElement>(surrogate.RawJson);

    public JsonElementSurrogate ConvertToSurrogate(in JsonElement value) => new()
    {
        RawJson = value.GetRawText(),
    };
}
