namespace Mohist.Server.Issue.Grains.Coordinator;

/// <summary>
/// issue-417 D2: narrow Project-side participant interface consumed only
/// by the binding coordinator. Mirrors
/// <see cref="IIssueBindingParticipant"/> but for the single
/// repository-removal command. The coordinator is the only caller;
/// ArchTest guards against bypass.
/// </summary>
public interface IProjectBindingParticipant : IGrainWithStringKey
{
    Task<ProjectBindingParticipantOutcome> RemoveRepositoryAsync(
        RepositoryCommandPayload.Remove payload,
        string commandId,
        long? expectedRevision);
}

public enum ProjectBindingParticipantOutcome
{
    Removed = 0,
    AlreadyApplied = 1,
}