using System.Text.Json.Serialization;

namespace Mohist.Server.Label.Services;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LabelOrigin
{
    System,
    User
}

public sealed record LabelDefinition(
    string Key,
    string Description,
    LabelOrigin Origin,
    IReadOnlyList<string>? SupportedValues = null);
