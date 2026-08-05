
namespace Mohist.Server.Slack.Services;

public interface ISlackConfigurationCredentialPort
{
    Task<SlackConfigurationCredentialRotationResult> RotateAsync(
        SlackConfigurationCredentialPair credentials,
        CancellationToken ct = default);
}

public sealed record SlackConfigurationCredentialPair(string AccessToken, string RefreshToken)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(AccessToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(RefreshToken);
    }
}

public sealed record SlackConfigurationCredentialRotationResult(
    SlackConfigurationCredentialRotationOutcome Outcome,
    SlackConfigurationCredentialPair? Credentials = null,
    string? WorkspaceTeamId = null,
    DateTimeOffset? ExpiresAt = null,
    string? ErrorClass = null);

public enum SlackConfigurationCredentialRotationOutcome
{
    Succeeded,
    DefiniteFailure,
    Unknown,
}

public sealed class FakeSlackConfigurationCredentialPort : ISlackConfigurationCredentialPort
{
    private readonly Queue<SlackConfigurationCredentialRotationResult> _results = [];

    public List<SlackConfigurationCredentialPair> Requests { get; } = [];

    public void Enqueue(SlackConfigurationCredentialRotationResult result) => _results.Enqueue(result);

    public Task<SlackConfigurationCredentialRotationResult> RotateAsync(
        SlackConfigurationCredentialPair credentials,
        CancellationToken ct = default)
    {
        credentials.Validate();
        Requests.Add(credentials);
        return Task.FromResult(_results.Count > 0
            ? _results.Dequeue()
            : new SlackConfigurationCredentialRotationResult(SlackConfigurationCredentialRotationOutcome.DefiniteFailure));
    }
}
