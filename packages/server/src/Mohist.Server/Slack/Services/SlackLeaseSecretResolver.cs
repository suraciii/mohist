using System.Text;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Infrastructure.Security.Secrets;

namespace Mohist.Server.Slack.Services;

/// <summary>
/// Resolves a stored secret address to its plaintext token, in-process only.
/// The lease core depends on this narrow seam rather than the full secret
/// store so the token never flows through state / list / discovery DTOs.
/// </summary>
public interface ISlackLeaseSecretResolver
{
    Task<string?> LoadAsync(SecretStoreAddress address, CancellationToken ct = default);
}

public sealed class SlackLeaseSecretResolver(ISecretStore secretStore) : ISlackLeaseSecretResolver, IScopedService
{
    public async Task<string?> LoadAsync(SecretStoreAddress address, CancellationToken ct = default)
    {
        var plaintext = await secretStore.LoadAsync(address, ct);
        return plaintext is null ? null : Encoding.UTF8.GetString(plaintext);
    }
}
