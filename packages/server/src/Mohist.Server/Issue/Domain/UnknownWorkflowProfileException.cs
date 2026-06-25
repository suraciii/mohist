namespace Mohist.Server.Issue.Domain;

/// <summary>
/// Thrown by the issue grain when a caller asks to apply a workflow profile
/// id that is not registered in <c>IssueWorkflowProfileRegistry</c>. The
/// route layer translates this into a 400 response (unknown profile id).
/// </summary>
public sealed class UnknownWorkflowProfileException : ArgumentException
{
    public string ProfileId { get; }

    public UnknownWorkflowProfileException(string profileId)
        : base($"Unknown workflow profile '{profileId}'")
    {
        ProfileId = profileId;
    }
}
