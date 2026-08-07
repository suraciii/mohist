using Microsoft.AspNetCore.Http;
using Mohist.Server.Auth.Identity;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Slack.Services;

/// <summary>
/// Authenticates a loopback adapter request as a Mohist operator. The
/// auth middleware has already resolved a Principal into
/// <see cref="HttpContext.Items"/>; this authenticator additionally
/// requires the explicit <c>X-Mohist-Operator-Id</c> header, whose value
/// is what the lease core records and audits against. Returns null when
/// either is missing.
/// </summary>
public interface ISlackAdapterOperatorAuthenticator
{
    Task<string?> AuthenticateAsync(HttpContext context, CancellationToken ct = default);
}

/// <summary>
/// Operator authentication for the Socket lease transport: the resolved
/// Principal proves authentication, and the <c>X-Mohist-Operator-Id</c>
/// header supplies the explicit identity every lease entry point
/// requires. No network involved — pure context/header checks.
/// </summary>
public sealed class SlackAdapterOperatorAuthenticator
    : ISlackAdapterOperatorAuthenticator, IScopedService
{
    public const string OperatorIdHeaderName = "X-Mohist-Operator-Id";

    public Task<string?> AuthenticateAsync(HttpContext context, CancellationToken ct = default)
    {
        if (!context.Items.TryGetValue(MohistPrincipal.HttpContextItemKey, out var value)
            || value is not MohistPrincipal)
        {
            return Task.FromResult<string?>(null);
        }

        if (!context.Request.Headers.TryGetValue(OperatorIdHeaderName, out var values)
            || values.Count != 1)
        {
            return Task.FromResult<string?>(null);
        }

        var operatorId = values[0]?.Trim();
        return Task.FromResult(string.IsNullOrWhiteSpace(operatorId) ? null : operatorId);
    }
}
