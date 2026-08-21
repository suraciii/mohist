using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
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
    private static readonly JsonSerializerOptions ManagerProxyJsonOptions = new(JsonSerializerDefaults.Web);

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
        // Manager executions use a Runner-owned credential proxy. The CLI
        // process never receives a bearer, so a same-user process cannot
        // recover it from /proc or a child environment.
        if (_managerMode && _credentials.TryResolveManagerBroker() is { } brokerPath)
            return await SendThroughManagerBrokerAsync(request, brokerPath, cancellationToken).ConfigureAwait(false);

        // A request can only be sent once, so the transport always
        // receives a clone; the credential is attached to it when
        // resolvable and allowed for the destination.
        var forwarded = await CloneAsync(request, cancellationToken).ConfigureAwait(false);
        if (_managerMode)
            forwarded.Headers.TryAddWithoutValidation(ManagerCapabilityCatalog.ManagerModeHeader, "1");
        var credential = await _credentials.TryResolveAllowedAsync(forwarded.RequestUri, _managerMode).ConfigureAwait(false);
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

    private static async Task<HttpResponseMessage> SendThroughManagerBrokerAsync(
        HttpRequestMessage request,
        string brokerPath,
        CancellationToken cancellationToken)
    {
        if (request.RequestUri is not { IsAbsoluteUri: true } destination)
            throw new HttpRequestException("Manager credential proxy requires an absolute request URI.");
        if (request.Headers.Authorization is not null)
            throw new HttpRequestException("Manager requests must not provide an Authorization header.");

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [ManagerCapabilityCatalog.ManagerModeHeader] = "1",
        };
        foreach (var (name, values) in request.Headers)
            if (!name.Equals("authorization", StringComparison.OrdinalIgnoreCase))
                headers[name] = string.Join(", ", values);
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (request.Content is not null)
            foreach (var (name, values) in request.Content.Headers)
                headers[name] = string.Join(", ", values);

        var payload = JsonSerializer.SerializeToUtf8Bytes(new ManagerProxyRequest(
            request.Method.Method,
            destination.ToString(),
            headers,
            body is null ? null : Convert.ToBase64String(body)), ManagerProxyJsonOptions);
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(brokerPath), cancellationToken).ConfigureAwait(false);
        await socket.SendAsync(payload, SocketFlags.None, cancellationToken).ConfigureAwait(false);
        socket.Shutdown(SocketShutdown.Send);

        using var responseBuffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var received = await socket.ReceiveAsync(chunk, SocketFlags.None, cancellationToken).ConfigureAwait(false);
            if (received == 0) break;
            responseBuffer.Write(chunk, 0, received);
            if (responseBuffer.Length > 16 * 1024 * 1024)
                throw new HttpRequestException("Manager credential proxy response exceeded its limit.");
        }
        var response = JsonSerializer.Deserialize<ManagerProxyResponse>(responseBuffer.ToArray(), ManagerProxyJsonOptions)
            ?? throw new HttpRequestException("Manager credential proxy returned an invalid response.");
        var result = new HttpResponseMessage((System.Net.HttpStatusCode)response.Status)
        {
            RequestMessage = request,
            Content = new ByteArrayContent(response.BodyBase64 is null ? [] : Convert.FromBase64String(response.BodyBase64)),
        };
        foreach (var (name, value) in response.Headers ?? [])
        {
            if (!result.Headers.TryAddWithoutValidation(name, value))
                result.Content.Headers.TryAddWithoutValidation(name, value);
        }
        return result;
    }

    private sealed record ManagerProxyRequest(
        string Method,
        string Url,
        Dictionary<string, string> Headers,
        string? BodyBase64);

    private sealed record ManagerProxyResponse(
        int Status,
        Dictionary<string, string>? Headers,
        string? BodyBase64);

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
