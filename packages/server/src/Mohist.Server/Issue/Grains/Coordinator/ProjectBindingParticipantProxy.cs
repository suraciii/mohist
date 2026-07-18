using Mohist.Server.Project.Domain;

namespace Mohist.Server.Issue.Grains.Coordinator;

/// <summary>
/// issue-417 D2: adapts <see cref="Mohist.Server.Project.Grains.IProjectGrain"/>
/// to the narrow <see cref="IProjectBindingParticipant"/> surface the
/// coordinator consumes. Mirrors <see cref="IssueBindingParticipantProxy"/>
/// but for the single repository-removal command. Translates
/// <see cref="Mohist.Server.Project.Domain.RepositoryInUseException"/>,
/// <see cref="Mohist.Server.Project.Domain.ProjectRepositoryNotFoundException"/>,
/// and <see cref="Mohist.Server.Project.Domain.ProjectRepositoryStaleRevisionException"/>
/// into the same exception types the coordinator already maps, so the
/// coordinator's result mapping stays uniform across both participants.
/// </summary>
public class ProjectBindingParticipantProxy : Grain, IProjectBindingParticipant
{
    private readonly IGrainFactory _grains;

    public ProjectBindingParticipantProxy(IGrainFactory grains)
    {
        _grains = grains;
    }

    private Mohist.Server.Project.Grains.IProjectGrain ProjectGrain(string projectId) =>
        _grains.GetGrain<Mohist.Server.Project.Grains.IProjectGrain>(projectId);

    public async Task<ProjectBindingParticipantOutcome> RemoveRepositoryAsync(
        RepositoryCommandPayload.Remove payload,
        string commandId,
        long? expectedRevision)
    {
        try
        {
            await BindingParticipantProbe.BeforeParticipantAsync(
                BindingParticipantProbeKind.Remove,
                payload.ProjectId,
                commandId);
            var outcome = await ProjectGrain(payload.ProjectId).RemoveRepositoryWithReceiptAsync(
                payload.RepositoryName,
                commandId,
                expectedRevision);
            return outcome switch
            {
                Mohist.Server.Project.Grains.ProjectRepositoryRemovalOutcome.Removed => ProjectBindingParticipantOutcome.Removed,
                Mohist.Server.Project.Grains.ProjectRepositoryRemovalOutcome.AlreadyApplied => ProjectBindingParticipantOutcome.AlreadyApplied,
                _ => throw new InvalidOperationException(
                    $"Unexpected removal outcome {outcome} for project '{payload.ProjectId}' repository '{payload.RepositoryName}'"),
            };
        }
        catch (ProjectRepositoryStaleRevisionException)
        {
            throw;
        }
        catch (ProjectRepositoryNotFoundException)
        {
            throw;
        }
        catch (RepositoryInUseException)
        {
            throw;
        }
    }
}