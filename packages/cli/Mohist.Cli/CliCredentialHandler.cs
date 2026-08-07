using System.Net.Http.Headers;

namespace Mohist.Cli;

/// <summary>
/// Attaches <c>Authorization: Bearer &lt;token&gt;</c> to every command
/// request when a credential is resolvable — the single injection point
/// covering all mo commands, not just the ones that used to thread
/// headers by hand. The machine-local admin credential (file or
/// <c>MOHIST_ADMIN_TOKEN</c>) is only ever sent to loopback
/// destinations; an explicit <c>MOHIST_TOKEN</c> may target any server.
/// Requests without a resolvable credential (or with an existing
/// Authorization header) pass through untouched. A request can only be
/// sent once, so every forwarded request is a clone with buffered
/// content.
/// </summary>
internal sealed class CliCredentialHandler : HttpMessageHandler
{
    private readonly CliCredentialProvider _credentials;
    private readonly HttpClient _transport;

    public CliCredentialHandler(CliCredentialProvider credentials, HttpClient transport)
    {
        _credentials = credentials;
        _transport = transport;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // A request can only be sent once, so the transport always
        // receives a clone; the credential is attached to it when
        // resolvable and allowed for the destination.
        var forwarded = await CloneAsync(request, cancellationToken).ConfigureAwait(false);
        if (request.Headers.Authorization is null
            && request.RequestUri is { } destination
            && await ResolveCredentialAsync().ConfigureAwait(false) is { } credential
            && (!credential.MachineLocal || IsLoopback(destination)))
        {
            forwarded.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", credential.Token);
        }

        return await _transport.SendAsync(forwarded, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CliCredential?> ResolveCredentialAsync()
    {
        try
        {
            return await _credentials.TryResolveAsync().ConfigureAwait(false);
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
