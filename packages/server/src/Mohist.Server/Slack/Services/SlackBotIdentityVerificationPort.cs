using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Slack.Services;

public interface ISlackBotIdentityVerificationPort
{
    Task<SlackBotIdentityVerificationResult> VerifyAsync(
        SlackBotIdentityVerificationRequest request,
        CancellationToken ct = default);
}

public sealed record SlackBotIdentityVerificationRequest(string BotToken);

public sealed record SlackBotIdentityVerificationResult(
    bool Verified,
    string? WorkspaceTeamId = null,
    string? BotUserId = null,
    string? AppId = null,
    IReadOnlySet<string>? GrantedScopes = null,
    string? ErrorClass = null);

public sealed class UnavailableSlackBotIdentityVerificationPort : ISlackBotIdentityVerificationPort, IScopedService
{
    public Task<SlackBotIdentityVerificationResult> VerifyAsync(
        SlackBotIdentityVerificationRequest request,
        CancellationToken ct = default) =>
        throw new NotSupportedException("Slack Bot identity verification is not connected in this slice.");
}

public sealed class FakeSlackBotIdentityVerificationPort : ISlackBotIdentityVerificationPort
{
    public List<SlackBotIdentityVerificationRequest> Requests { get; } = [];
    public SlackBotIdentityVerificationResult Result { get; set; } = new(false);

    public Task<SlackBotIdentityVerificationResult> VerifyAsync(
        SlackBotIdentityVerificationRequest request,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.BotToken);
        Requests.Add(request);
        return Task.FromResult(Result);
    }
}
