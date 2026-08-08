using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mohist.Cli;

/// <summary>
/// Attaches <c>Authorization: Bearer &lt;token&gt;</c> to every command
/// request when a credential is resolvable — the single injection point
/// covering all mo commands, not just the ones that used to thread
/// headers by hand. The machine-local admin credential (file or
/// <c>MOHIST_ADMIN_TOKEN</c>) is only ever sent to loopback
/// destinations; an explicit <c>MOHIST_TOKEN</c> or a credentials.json
/// session may target any server. A 401 on a request that carried a
/// credentials.json session rolls the session forward once (refresh
/// rotation) and retries; when the
/// refresh fails the user is told to sign in again. Requests without a
/// resolvable credential (or with an existing Authorization header)
/// pass through untouched. A request can only be sent once, so every
/// forwarded request is a clone with buffered content.
/// </summary>
internal sealed class CliCredentialHandler : HttpMessageHandler
{
    private readonly CliCredentialProvider _credentials;
    private readonly HttpClient _transport;
    private readonly CliCredentialFile _credentialFile;
    private readonly TextWriter? _error;

    public CliCredentialHandler(
        CliCredentialProvider credentials,
        HttpClient transport,
        IFileSystem fileSystem,
        Func<string> getUserHome,
        TextWriter? error = null)
    {
        _credentials = credentials;
        _transport = transport;
        _credentialFile = new CliCredentialFile(fileSystem, CliCredentialFile.PathFor(getUserHome));
        _error = error;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // A request can only be sent once, so the transport always
        // receives a clone; the credential is attached to it when
        // resolvable and allowed for the destination.
        var forwarded = await CloneAsync(request, cancellationToken).ConfigureAwait(false);
        var credential = await ResolveCredentialAsync(forwarded.RequestUri).ConfigureAwait(false);
        if (credential is not null
            && forwarded.RequestUri is { } destination
            && request.Headers.Authorization is null
            && (!credential.MachineLocal || IsLoopback(destination)))
        {
            forwarded.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", credential.Token);
        }

        var response = await _transport.SendAsync(forwarded, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized
            && credential is { Source: CliCredentialSource.CredentialFile, Stored: not null }
            && forwarded.RequestUri is { } destination2)
        {
            var refreshed = await TryRefreshAsync(credential.Stored, destination2, cancellationToken)
                .ConfigureAwait(false);
            if (refreshed is not null)
            {
                var retry = await CloneAsync(forwarded, cancellationToken).ConfigureAwait(false);
                retry.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", refreshed.AccessToken);
                return await _transport.SendAsync(retry, cancellationToken).ConfigureAwait(false);
            }

            _error?.WriteLine("Session expired. Run 'mo auth login' to sign in again.");
        }

        return response;
    }

    /// <summary>
    /// Rolls the stored session forward via the refresh endpoint (which
    /// carries its own credential and is exempt from auth resolution).
    /// The new pair is persisted before the original request is retried;
    /// a persistence failure still returns the fresh access token so the
    /// command can succeed — the next command then re-logs in.
    /// </summary>
    private async Task<StoredCliCredential?> TryRefreshAsync(
        StoredCliCredential stored,
        Uri destination,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(destination, "/api/auth/token"))
            {
                Content = JsonContent.Create(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = stored.RefreshToken,
                }),
            };
            using var response = await _transport.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            var node = await JsonNode.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var data = node?["data"];
            if (data is null)
                return null;

            var accessToken = data["accessToken"]?.GetValue<string>();
            var refreshToken = data["refreshToken"]?.GetValue<string>();
            if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(refreshToken))
                return null;

            var updated = stored with
            {
                AccessToken = accessToken,
                RefreshToken = refreshToken,
                AccessExpiresAt = data["accessExpiresAt"]?.GetValue<DateTimeOffset>() ?? default,
                RefreshExpiresAt = data["refreshExpiresAt"]?.GetValue<DateTimeOffset>() ?? default,
            };
            try
            {
                await _credentialFile.SaveAsync(updated).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // Best-effort persistence; the request still retries with
                // the fresh access token.
            }

            return updated;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return null;
        }
    }

    private async Task<CliCredential?> ResolveCredentialAsync(Uri? destination)
    {
        try
        {
            return await _credentials.TryResolveAsync(destination).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static async Task<HttpRequestMessage> CloneAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
        };
        foreach (var (name, value) in request.Headers)
            clone.Headers.TryAddWithoutValidation(name, value);
        if (request.Content is not null)
        {
            var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            clone.Content = new ByteArrayContent(bytes);
            foreach (var (name, value) in request.Content.Headers)
                clone.Content.Headers.TryAddWithoutValidation(name, value);
        }

        return clone;
    }

    private static bool IsLoopback(Uri destination) =>
        string.Equals(destination.Host, "localhost", StringComparison.OrdinalIgnoreCase)
        || (System.Net.IPAddress.TryParse(destination.Host, out var address)
            && System.Net.IPAddress.IsLoopback(address));
}
