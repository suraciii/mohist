using System.Security.Cryptography;
using System.Text;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Infrastructure.Security.Secrets;

namespace Mohist.Server.Slack.Services;

/// <summary>
/// Signs Slack action canonical forms with the Connection's bot token.
/// Action services own payload validation and canonical field ordering; this
/// helper owns only secret loading and the HMAC comparison shared by those
/// services.
/// </summary>
public interface ISlackActionSigner
{
    Task<string?> TrySignAsync(AgentConnection connection, string canonical, CancellationToken ct = default);
    Task<bool> VerifyAsync(AgentConnection connection, string canonical, string? signature, CancellationToken ct = default);
}

internal sealed class SlackActionSigningHelper : ISlackActionSigner, IScopedService
{
    private readonly ISecretStore _secrets;

    public SlackActionSigningHelper(ISecretStore secrets) => _secrets = secrets;

    public async Task<string?> TrySignAsync(
        AgentConnection connection,
        string canonical,
        CancellationToken ct = default)
    {
        var key = await LoadSigningKeyAsync(connection, ct);
        return key is null
            ? null
            : Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(canonical)));
    }

    public async Task<bool> VerifyAsync(
        AgentConnection connection,
        string canonical,
        string? signature,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(signature))
            return false;

        var expected = await TrySignAsync(connection, canonical, ct);
        return expected is not null && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(signature));
    }

    private async Task<byte[]?> LoadSigningKeyAsync(AgentConnection connection, CancellationToken ct)
    {
        try
        {
            var token = await _secrets.LoadAsync(
                new SecretStoreAddress(connection.ProjectId, connection.Id, SecretKind.BotToken), ct);
            return token is { Length: > 0 } ? token : null;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }
}
