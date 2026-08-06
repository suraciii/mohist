using System.Security.Cryptography;
using System.Text;

namespace Mohist.Server.GitHub;

/// <summary>
/// Verifies the GitHub-style X-Hub-Signature-256 webhook header
/// (HMAC-SHA256 over the raw request body, hex-encoded as
/// "sha256=&lt;hex&gt;"). Mirrors the signing side in
/// <see cref="Mohist.Server.Notifications.HermesWebhookClient"/>.
/// </summary>
public static class GitHubWebhookSignature
{
    public const string SignatureHeader = "X-Hub-Signature-256";

    public static bool Verify(byte[] payload, string secret, string? signatureHeader)
    {
        if (payload is null || string.IsNullOrWhiteSpace(secret))
            return false;
        const string prefix = "sha256=";
        if (string.IsNullOrWhiteSpace(signatureHeader) || !signatureHeader.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        var hex = signatureHeader[prefix.Length..].Trim();
        if (hex.Length != 64 || !TryParseHex(hex, out var provided))
            return false;
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), payload);
        return CryptographicOperations.FixedTimeEquals(provided, expected);
    }

    private static bool TryParseHex(string hex, out byte[] bytes)
    {
        bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            var high = HexValue(hex[i * 2]);
            var low = HexValue(hex[i * 2 + 1]);
            if (high < 0 || low < 0)
                return false;
            bytes[i] = (byte)((high << 4) | low);
        }
        return true;
    }

    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };
}
