namespace Mohist.Server.Workflow.Grains;

internal static class ApprovalOperatorValidation
{
    public const int MaxLength = 100;

    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var normalized = raw.Trim();
        if (normalized.Length > MaxLength)
            throw new ArgumentException($"Approval operator must be {MaxLength} characters or fewer.", nameof(raw));

        return normalized;
    }
}
