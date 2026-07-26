namespace Mohist.Server.Issue.Grains.Coordinator;

/// <summary>
/// narrow Issue-side participant interface consumed only
/// by the binding coordinator. Only the coordinator grain may depend on
/// this interface (enforced by ArchTest); production routes, services,
/// and other grains MUST go through <see cref="IIssueRepositoryCoordinatorGrain"/>
/// instead of calling the underlying <c>IIssueGrain</c> for create,
/// reassign, or reopen. The interface exposes idempotent participant
/// commands: a duplicated <c>commandId</c> with matching kind / repository
/// returns <see cref="IssueBindingParticipantOutcome.AlreadyApplied"/>;
/// a stale <c>expectedRevision</c> throws
/// <see cref="Mohist.Server.Issue.Domain.IssueRepositoryStaleRevisionException"/>;
/// a missing target declaration throws
/// <see cref="Mohist.Server.Issue.Domain.IssueRepositoryUnknownException"/>;
/// a missing target on reopen throws
/// <see cref="Mohist.Server.Issue.Domain.IssueRepositoryMissingOnReopenException"/>.
/// </summary>
public interface IIssueBindingParticipant : IGrainWithStringKey
{
    Task<IssueBindingParticipantOutcome> CreateAsync(RepositoryCommandPayload.Create payload, string commandId, long? expectedRevision);

    Task<IssueBindingParticipantOutcome> ChangeRepositoryAsync(RepositoryCommandPayload.Change payload, string commandId, long? expectedRevision);

    Task<IssueBindingParticipantOutcome> ReopenAsync(RepositoryCommandPayload.Reopen payload, string commandId, long? expectedRevision);
}

public enum IssueBindingParticipantOutcome
{
    Applied = 0,
    AlreadyApplied = 1,
}