using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Security.Secrets;

namespace Mohist.Server.Infrastructure.PublicApi;

/// <summary>
/// Creates and verifies the stateless cursor used by the public Session
/// event route. The key is encrypted by the server secret store and is
/// loaded once for each request signer, so replacing the persisted key
/// immediately invalidates previously issued cursors.
/// </summary>
public sealed class PublicSessionEventCursorCodec
{
    public const int CurrentVersion = 1;
    public const int KeyLength = 32;
    private const int SignatureLength = 32;

    private readonly ISecretStore _secrets;
    private readonly SemaphoreSlim _keyCreationGate = new(1, 1);

    public PublicSessionEventCursorCodec(ISecretStore secrets)
    {
        _secrets = secrets;
    }

    public static SecretStoreAddress SecretAddress =>
        SecretStoreAddress.ForServer(SecretKind.PublicApiCursorKey);

    /// <summary>Loads or creates the deployment-wide cursor signing key.</summary>
    public async Task<PublicSessionEventCursorSigner> OpenAsync(CancellationToken ct = default)
    {
        var key = await EnsureKeyAsync(ct);
        return new PublicSessionEventCursorSigner(key);
    }

    private async Task<byte[]> EnsureKeyAsync(CancellationToken ct)
    {
        var existing = await _secrets.LoadAsync(SecretAddress, ct);
        if (existing is not null)
        {
            ValidateKey(existing);
            return existing;
        }

        await _keyCreationGate.WaitAsync(ct);
        try
        {
            existing = await _secrets.LoadAsync(SecretAddress, ct);
            if (existing is not null)
            {
                ValidateKey(existing);
                return existing;
            }

            var key = RandomNumberGenerator.GetBytes(KeyLength);
            await _secrets.StoreAsync(SecretAddress, key, ct);
            return key;
        }
        finally
        {
            _keyCreationGate.Release();
        }
    }

    private static void ValidateKey(byte[] key)
    {
        if (key.Length != KeyLength)
        {
            throw new SecretStoreKeyException(
                "The persisted public API cursor key has an invalid length.");
        }
    }

    public sealed class PublicSessionEventCursorSigner
    {
        private readonly byte[] _key;

        internal PublicSessionEventCursorSigner(byte[] key)
        {
            _key = key;
        }

        public string Encode(PublicSessionEventCursorPayload payload)
        {
            ArgumentNullException.ThrowIfNull(payload);
            ValidatePayload(payload);

            var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JSON.PublicApi);
            var signature = HMACSHA256.HashData(_key, payloadBytes);
            var token = new byte[payloadBytes.Length + signature.Length];
            Buffer.BlockCopy(payloadBytes, 0, token, 0, payloadBytes.Length);
            Buffer.BlockCopy(signature, 0, token, payloadBytes.Length, signature.Length);
            return Base64UrlEncode(token);
        }

        /// <summary>
        /// Verifies the signature and the route binding before returning a
        /// cursor. A false result is deliberately indistinguishable for
        /// malformed, tampered, or cross-bound tokens.
        /// </summary>
        public bool TryDecode(
            string token,
            string projectId,
            string sessionId,
            out PublicSessionEventCursorPayload? payload)
        {
            payload = null;
            if (string.IsNullOrEmpty(token)
                || string.IsNullOrEmpty(projectId)
                || string.IsNullOrEmpty(sessionId)
                || !TryBase64UrlDecode(token, out var tokenBytes)
                || tokenBytes.Length <= SignatureLength)
            {
                return false;
            }

            var payloadLength = tokenBytes.Length - SignatureLength;
            var payloadBytes = tokenBytes.AsSpan(0, payloadLength);
            var signature = tokenBytes.AsSpan(payloadLength, SignatureLength);
            var expected = HMACSHA256.HashData(_key, payloadBytes);
            if (!CryptographicOperations.FixedTimeEquals(signature, expected))
            {
                return false;
            }

            if (!TryParsePayload(payloadBytes, out var parsed)
                || parsed is null
                || !string.Equals(parsed.ProjectId, projectId, StringComparison.Ordinal)
                || !string.Equals(parsed.SessionId, sessionId, StringComparison.Ordinal))
            {
                return false;
            }

            payload = parsed;
            return true;
        }

        private static void ValidatePayload(PublicSessionEventCursorPayload payload)
        {
            if (string.IsNullOrEmpty(payload.ProjectId)
                || string.IsNullOrEmpty(payload.SessionId)
                || payload.Generation <= 0
                || payload.AfterPosition < 0
                || payload.Version != CurrentVersion)
            {
                throw new ArgumentException("The public Session event cursor payload is invalid.", nameof(payload));
            }
        }

        private static bool TryParsePayload(
            ReadOnlySpan<byte> bytes,
            out PublicSessionEventCursorPayload? payload)
        {
            payload = null;
            try
            {
                var reader = new Utf8JsonReader(
                    bytes,
                    new JsonReaderOptions
                    {
                        AllowTrailingCommas = false,
                        CommentHandling = JsonCommentHandling.Disallow,
                    });
                if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                {
                    return false;
                }

                string? projectId = null;
                string? sessionId = null;
                long? generation = null;
                long? afterPosition = null;
                int? version = null;
                var seen = new HashSet<string>(StringComparer.Ordinal);

                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    if (reader.TokenType != JsonTokenType.PropertyName)
                    {
                        return false;
                    }

                    var name = reader.GetString();
                    if (name is null || !seen.Add(name) || !reader.Read())
                    {
                        return false;
                    }

                    switch (name)
                    {
                        case "projectId" when reader.TokenType == JsonTokenType.String:
                            projectId = reader.GetString();
                            break;
                        case "sessionId" when reader.TokenType == JsonTokenType.String:
                            sessionId = reader.GetString();
                            break;
                        case "generation" when reader.TokenType == JsonTokenType.Number
                            && reader.TryGetInt64(out var parsedGeneration):
                            generation = parsedGeneration;
                            break;
                        case "afterPosition" when reader.TokenType == JsonTokenType.Number
                            && reader.TryGetInt64(out var parsedAfterPosition):
                            afterPosition = parsedAfterPosition;
                            break;
                        case "version" when reader.TokenType == JsonTokenType.Number
                            && reader.TryGetInt32(out var parsedVersion):
                            version = parsedVersion;
                            break;
                        default:
                            return false;
                    }
                }

                if (reader.TokenType != JsonTokenType.EndObject || reader.Read()
                    || projectId is null
                    || sessionId is null
                    || generation is null
                    || afterPosition is null
                    || version is null)
                {
                    return false;
                }

                var candidate = new PublicSessionEventCursorPayload(
                    projectId,
                    sessionId,
                    generation.Value,
                    afterPosition.Value,
                    version.Value);
                ValidatePayload(candidate);
                payload = candidate;
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private static string Base64UrlEncode(byte[] bytes) =>
            Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');

        private static bool TryBase64UrlDecode(string token, out byte[] bytes)
        {
            bytes = [];
            if (token.Contains('=')
                || token.Any(character =>
                    !(character is >= 'A' and <= 'Z'
                        or >= 'a' and <= 'z'
                        or >= '0' and <= '9'
                        or '-'
                        or '_'))
                || token.Length % 4 == 1)
            {
                return false;
            }

            var base64 = token.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + ((4 - base64.Length % 4) % 4), '=');
            try
            {
                bytes = Convert.FromBase64String(base64);
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }
    }
}

public sealed record PublicSessionEventCursorPayload(
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("generation")] long Generation,
    [property: JsonPropertyName("afterPosition")] long AfterPosition,
    [property: JsonPropertyName("version")] int Version);
