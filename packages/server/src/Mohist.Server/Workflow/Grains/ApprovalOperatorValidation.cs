namespace Mohist.Server.Workflow.Grains;

/// <summary>
/// Validates a declarative approval operator the same way the comment author
/// is validated: required, trimmed of surrounding whitespace, and capped at
/// 100 characters. Used by <see cref="WorkflowGrain.ApproveAsync(string)"/>
/// and <see cref="WorkflowGrain.RequestChangesAsync(string, string)"/> to
/// enforce the contract at the grain boundary; route handlers and the CLI
/// mirror the same validation before issuing the call.
/// </summary>
internal static class ApprovalOperatorValidation
{
    public const int MaxLength = 100;

    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new ArgumentException("Approval operator is required.", nameof(raw));

        var normalized = raw.Trim();
        if (normalized.Length > MaxLength)
            throw new ArgumentException($"Approval operator must be {MaxLength} characters or fewer.", nameof(raw));

        return normalized;
    }
}
