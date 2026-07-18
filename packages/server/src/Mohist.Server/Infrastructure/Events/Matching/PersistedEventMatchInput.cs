namespace Mohist.Server.Infrastructure.Events.Matching;

public sealed class PersistedEventMatchInput : EventMatchInput
{
    private readonly IReadOnlyDictionary<string, string> _attributes;

    public PersistedEventMatchInput(
        string type,
        string source,
        string? subject,
        IReadOnlyDictionary<string, string> extensions)
    {
        var attributes = new Dictionary<string, string>(extensions, StringComparer.Ordinal)
        {
            ["type"] = type,
            ["source"] = source,
        };
        if (subject is not null)
            attributes["subject"] = subject;
        _attributes = attributes;
    }

    public string GetValue(string attribute) =>
        _attributes.TryGetValue(attribute, out var value) ? value : string.Empty;

    public bool Has(string attribute) => _attributes.ContainsKey(attribute);
}
