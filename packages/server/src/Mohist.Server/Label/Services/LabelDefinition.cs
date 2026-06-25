namespace Mohist.Server.Label.Services;

public sealed record LabelDefinition(
    string Key,
    string Description,
    IReadOnlyList<string>? SupportedValues = null);
