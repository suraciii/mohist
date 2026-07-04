namespace Mohist.Server.Issue.Domain;

/// <summary>
/// Thrown by <see cref="IssueGrain.CreateAsync"/> when one or more entries in
/// the create-time <c>prerequisiteNumbers</c> array fail validation
/// (nonexistent issue in the same project, self-reference, …). The route
/// layer translates this into a 400 response with the offending number so
/// callers can surface a precise error.
/// <para>
/// Atomicity is structural, not compensatory: the grain validates and applies
/// prerequisites <em>before</em> <c>SaveIssueAsync</c>, so this exception
/// always means the create left no persisted, readable issue behind. The
/// issue counter still consumes its allocated number (established counter
/// semantics — see design Risks).
/// </para>
/// </summary>
public sealed class PrerequisiteValidationException : ArgumentException
{
    public int OffendingNumber { get; }
    public string Reason { get; }

    private PrerequisiteValidationException(int offendingNumber, string reason, string message)
        : base(message)
    {
        OffendingNumber = offendingNumber;
        Reason = reason;
    }

    public static PrerequisiteValidationException NotFound(int number) =>
        new(number, "not_found", $"Issue #{number} not found");

    public static PrerequisiteValidationException SelfReference(int number) =>
        new(number, "self_reference", $"Issue cannot depend on itself (prerequisite #{number})");
}
