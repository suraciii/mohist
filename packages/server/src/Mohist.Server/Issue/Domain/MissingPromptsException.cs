using Orleans;

namespace Mohist.Server.Issue.Domain;

[GenerateSerializer]
public class MissingPromptsException : InvalidOperationException
{
    [Id(0)]
    public IReadOnlyList<string> MissingKeys { get; }

    public MissingPromptsException(IReadOnlyList<string> missingKeys)
        : base($"Unknown prompt template keys referenced in workflow: {string.Join(", ", missingKeys)}")
    {
        MissingKeys = missingKeys;
    }
}
