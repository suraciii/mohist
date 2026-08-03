using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Infrastructure.Slack;

public sealed record SlackWebLink(string Url, JsonElement Blocks);

public sealed class SlackWebLinkBuilder : IScopedService
{
    private readonly IOptions<SlackProviderOptions> _options;

    public SlackWebLinkBuilder(IOptions<SlackProviderOptions> options) => _options = options;

    public bool HasUsableExternalWebUrl => TryGetExternalWebUri(_options.Value, out _);

    public SlackWebLink? BuildOpenSession(string projectName, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(projectName) || string.IsNullOrWhiteSpace(sessionId))
            return null;

        var options = _options.Value;
        if (!TryGetExternalWebUri(options, out var externalWebUri))
            return null;

        var sessionUri = new UriBuilder(externalWebUri)
        {
            Path = $"{externalWebUri.AbsolutePath.TrimEnd('/')}/{Uri.EscapeDataString(projectName)}/sessions/{Uri.EscapeDataString(sessionId)}",
            Query = string.Empty,
            Fragment = string.Empty,
        }.Uri;
        var url = sessionUri.GetComponents(UriComponents.AbsoluteUri, UriFormat.UriEscaped);
        var blocks = JsonSerializer.SerializeToElement(new object[]
        {
            new
            {
                type = "actions",
                elements = new object[]
                {
                    new
                    {
                        type = "button",
                        text = new { type = "plain_text", text = "Open in Mohist" },
                        url,
                    },
                },
            },
        });
        return new SlackWebLink(url, blocks);
    }

    private static bool TryGetExternalWebUri(SlackProviderOptions options, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(options.ExternalWebUrl)
            || !Uri.TryCreate(options.ExternalWebUrl, UriKind.Absolute, out var candidate)
            || string.IsNullOrWhiteSpace(candidate.Host)
            || !string.IsNullOrEmpty(candidate.UserInfo)
            || !string.IsNullOrEmpty(candidate.Query)
            || !string.IsNullOrEmpty(candidate.Fragment)
            || IsLocalHost(candidate))
        {
            return false;
        }

        if (!string.Equals(candidate.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && (!string.Equals(candidate.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || !IsDevelopmentOriginAllowed(candidate, options.DevelopmentExternalWebUrlAllowlist)))
        {
            return false;
        }

        uri = candidate;
        return true;
    }

    private static bool IsDevelopmentOriginAllowed(Uri candidate, IEnumerable<string> allowlist)
    {
        var candidateOrigin = candidate.GetLeftPart(UriPartial.Authority);
        foreach (var entry in allowlist)
        {
            if (!Uri.TryCreate(entry, UriKind.Absolute, out var allowed)
                || !string.Equals(allowed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrEmpty(allowed.UserInfo))
            {
                continue;
            }

            if (string.Equals(candidateOrigin, allowed.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool IsLocalHost(Uri uri)
    {
        var host = uri.DnsSafeHost.TrimEnd('.');
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || !IPAddress.TryParse(host, out var address))
        {
            return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
        }

        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address))
            return true;

        var bytes = address.GetAddressBytes();
        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => bytes[0] == 0
                || bytes[0] == 10
                || bytes[0] == 127
                || (bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
                || (bytes[0] == 169 && bytes[1] == 254)
                || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                || (bytes[0] == 192 && bytes[1] == 168),
            AddressFamily.InterNetworkV6 => address.Equals(IPAddress.IPv6Any)
                || address.IsIPv6LinkLocal
                || address.IsIPv6SiteLocal
                || bytes[0] is 0xfc or 0xfd,
            _ => true,
        };
    }
}
