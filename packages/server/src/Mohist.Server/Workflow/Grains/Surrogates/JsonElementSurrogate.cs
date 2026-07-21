using System.Text.Json;
using Mohist.Server.Infrastructure;
using Orleans.Serialization;

namespace Mohist.Server.Workflow.Grains.Surrogates;

[GenerateSerializer]
public struct JsonElementSurrogate
{
    [Id(0)] public string RawJson;
}

[RegisterConverter]
public sealed class JsonElementSurrogateConverter : IConverter<JsonElement, JsonElementSurrogate>
{
    public JsonElement ConvertFromSurrogate(in JsonElementSurrogate surrogate) =>
        JSON.DeserializeElement(surrogate.RawJson).Clone();

    public JsonElementSurrogate ConvertToSurrogate(in JsonElement value) => new()
    {
        RawJson = value.ValueKind == JsonValueKind.Undefined ? "null" : value.GetRawText(),
    };
}
