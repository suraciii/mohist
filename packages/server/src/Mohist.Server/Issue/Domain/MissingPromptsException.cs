namespace Mohist.Server.Issue.Domain;

public class MissingPromptsException : InvalidOperationException
{
    public IReadOnlyList<string> MissingKeys { get; }

    public MissingPromptsException(IReadOnlyList<string> missingKeys)
        : base($"Unknown prompt template keys referenced in workflow: {string.Join(", ", missingKeys)}")
    {
        MissingKeys = missingKeys;
    }
}
