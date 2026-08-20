using System.Net.Http.Headers;
using Mohist.Workflow.Definition;

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
    private readonly CliCredentialSession _credentials;
    private readonly HttpClient _transport;
    private readonly bool _managerMode;

    public CliCredentialHandler(
        CliCredentialSession credentials,
        HttpClient transport,
        bool managerMode = false)
    {
        _credentials = credentials;
        _transport = transport;
        _managerMode = managerMode;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // A request can only be sent once, so the transport always
        // receives a clone; the credential is attached to it when
        // resolvable and allowed for the destination.
        var forwarded = await CloneAsync(request, cancellationToken).ConfigureAwait(false);
        if (_managerMode)
            forwarded.Headers.TryAddWithoutValidation(ManagerCapabilityCatalog.ManagerModeHeader, "1");
        var credential = await _credentials.TryResolveAllowedAsync(forwarded.RequestUri).ConfigureAwait(false);
        if (credential is not null
            && request.Headers.Authorization is null)
        {
            forwarded.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", credential.Token);
        }

        var response = await _transport.SendAsync(forwarded, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized
            && credential is { Source: CliCredentialSource.CredentialFile, Stored: not null }
            && forwarded.RequestUri is { } destination2)
        {
            var refreshed = await _credentials.TryRefreshAsync(credential, destination2, cancellationToken)
                .ConfigureAwait(false);
            if (refreshed is not null)
            {
                var retry = await CloneAsync(forwarded, cancellationToken).ConfigureAwait(false);
                retry.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", refreshed.AccessToken);
                return await _transport.SendAsync(retry, cancellationToken).ConfigureAwait(false);
            }

            _credentials.WriteExpiredSessionMessage();
        }

        return response;
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
}
