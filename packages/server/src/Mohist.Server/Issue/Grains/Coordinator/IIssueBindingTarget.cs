namespace Mohist.Server.Issue.Grains.Coordinator;

/// <summary>
/// issue-417 D2: narrow Issue-side receipt interface consumed only by
/// the binding coordinator and its proxy. Only the coordinator and
/// <c>IssueBindingParticipantProxy</c> may depend on this interface
/// (enforced by ArchTest); production routes, services, and other grains
/// MUST go through <see cref="IIssueRepositoryCoordinatorGrain"/> instead.
/// </summary>
public interface IIssueBindingTarget : IGrainWithStringKey
{
    Task<IssueBindingParticipantOutcome> CreateWithReceiptAsync(
        string projectId,
        int number,
        string title,
        string? body,
        IReadOnlyDictionary<string, string>? labels,
        string? priority,
        string repositoryRef,
        string? risk,
        bool isDraft,
        string[]? attachmentIds,
        string? workflowProfileId,
        int[]? prerequisiteNumbers,
        int? parentIssueNumber,
        string commandId,
        long? expectedRevision);

    Task<IssueBindingParticipantOutcome> ChangeRepositoryWithReceiptAsync(
        IssueChangeRepositoryCommand command,
        string commandId,
        long? expectedRevision);

    Task<IssueBindingParticipantOutcome> ReopenWithReceiptAsync(
        string commandId,
        long? expectedRevision);

    Task<long> GetRepositoryBindingRevisionAsync();
}
