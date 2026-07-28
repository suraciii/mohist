namespace Mohist.Server.Workflow.Domain;

/// <summary>
/// thrown by <see cref="Mohist.Server.Workflow.Services.WorkflowProfileManager"/>
/// when the resolved Profile or stage definition cannot be located.
/// Carries a decidable <see cref="Reason"/> so <c>WorkflowGrain.CommitAsync</c>
/// can branch on the type (or the discriminator) rather than on the
/// exception message text. Message wording is preserved verbatim from
/// the original <see cref="InvalidOperationException"/> sites so the
/// user-facing error surface is byte-identical.
/// </summary>
public sealed class WorkflowDefinitionResolutionException : Exception
{
    public enum ResolutionReason
    {
        NoCurrentDefinition = 0,
        NoStageDefinition = 1,
    }

    public WorkflowDefinitionResolutionException(ResolutionReason reason, string message)
        : base(message)
    {
        Reason = reason;
    }

    public ResolutionReason Reason { get; }
}
