using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal sealed class CliCredentialSession
{
    private readonly CliCredentialProvider _provider;
    private readonly HttpClient _transport;
    private readonly CliCredentialFile _file;
    private readonly TextWriter? _error;

    public CliCredentialSession(
        CliCredentialProvider provider,
        HttpClient transport,
        IFileSystem fileSystem,
        Func<string> getUserHome,
        TextWriter? error = null)
    {
        _provider = provider;
        _transport = transport;
        _file = new CliCredentialFile(fileSystem, CliCredentialFile.PathFor(getUserHome));
        _error = error;
    }

    public async Task<CliCredential?> TryResolveAllowedAsync(Uri? destination)
    {
        try
        {
            var credential = await _provider.TryResolveAsync(destination).ConfigureAwait(false);
            return credential is { MachineLocal: true } && (destination is null || !IsLoopback(destination))
                ? null
                : credential;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public async Task<StoredCliCredential?> TryRefreshAsync(
        CliCredential credential,
        Uri destination,
        CancellationToken cancellationToken)
    {
        if (credential is not { Source: CliCredentialSource.CredentialFile, Stored: not null })
            return null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(destination, "/api/auth/token"))
            {
                Content = JsonContent.Create(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = credential.Stored.RefreshToken,
                }),
            };
            using var response = await _transport.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            var node = await JsonNode.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var data = node?["data"];
            var accessToken = data?["accessToken"]?.GetValue<string>();
            var refreshToken = data?["refreshToken"]?.GetValue<string>();
            if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(refreshToken))
                return null;

            var updated = credential.Stored with
            {
                AccessToken = accessToken,
                RefreshToken = refreshToken,
                AccessExpiresAt = data?["accessExpiresAt"]?.GetValue<DateTimeOffset>() ?? default,
                RefreshExpiresAt = data?["refreshExpiresAt"]?.GetValue<DateTimeOffset>() ?? default,
            };
            try
            {
                await _file.SaveAsync(updated).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // The current request can still use the rotated access token.
            }
            return updated;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return null;
        }
    }

    public void WriteExpiredSessionMessage() =>
        _error?.WriteLine("Session expired. Run 'mo auth login' to sign in again.");

    private static bool IsLoopback(Uri destination) =>
        string.Equals(destination.Host, "localhost", StringComparison.OrdinalIgnoreCase)
        || (System.Net.IPAddress.TryParse(destination.Host, out var address)
            && System.Net.IPAddress.IsLoopback(address));
}
