using Microsoft.AspNetCore.Http;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Infrastructure.Security;

namespace Mohist.Server.Slack.Services;

/// <summary>
/// Authenticates a loopback adapter request as a Mohist operator. The
/// adapter must present the shared operator token <em>and</em> an explicit
/// operator identity; the returned identity is what the lease core records
/// and audits against. Returns null when either is missing or invalid.
/// </summary>
public interface ISlackAdapterOperatorAuthenticator
{
    Task<string?> AuthenticateAsync(IHeaderDictionary headers, CancellationToken ct = default);
}

/// <summary>
/// Operator authentication for the Socket lease transport: the shared
/// <see cref="OperatorCredential"/> token proves operator-ness, and the
/// <c>X-Mohist-Operator-Id</c> header supplies the explicit identity every
/// lease entry point requires. No network involved — pure header checks.
/// </summary>
public sealed class SlackAdapterOperatorAuthenticator(OperatorCredential credential)
    : ISlackAdapterOperatorAuthenticator, IScopedService
{
    public const string OperatorIdHeaderName = "X-Mohist-Operator-Id";

    public Task<string?> AuthenticateAsync(IHeaderDictionary headers, CancellationToken ct = default)
    {
        if (!credential.Authorizes(headers))
            return Task.FromResult<string?>(null);
        if (!headers.TryGetValue(OperatorIdHeaderName, out var values) || values.Count != 1)
            return Task.FromResult<string?>(null);

        var operatorId = values[0]?.Trim();
        return Task.FromResult(string.IsNullOrWhiteSpace(operatorId) ? null : operatorId);
    }
}
