using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Mohist.Server.Agent.Grains;

namespace Mohist.Server.Api.DirectApi;

public static class DirectApiWriteValidation
{
    public const int FingerprintVersion = 1;
    public const string LaunchCommand = "launch";

    public static IdempotencyKeyValidation ReadIdempotencyKey(IHeaderDictionary headers)
    {
        if (!headers.TryGetValue("Idempotency-Key", out var values) || values.Count == 0)
            return IdempotencyKeyValidation.Required;

        if (values.Count != 1)
            return IdempotencyKeyValidation.Invalid;

        var value = values[0];
        if (value is null)
            return IdempotencyKeyValidation.Invalid;
        if (value.Length is < 1 or > 128)
            return IdempotencyKeyValidation.Invalid;
        if (value.Any(character => character is < '\u0020' or > '\u007e'))
            return IdempotencyKeyValidation.Invalid;

        return new IdempotencyKeyValidation(IdempotencyKeyDisposition.Valid, value);
    }

    public static async Task<DirectApiTextBodyRead> ReadTextBodyAsync(
        Stream body,
        CancellationToken ct = default)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(
                body,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                },
                ct);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return DirectApiTextBodyRead.Invalid;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            string? text = null;
            foreach (var property in root.EnumerateObject())
            {
                if (!seen.Add(property.Name)
                    || !string.Equals(property.Name, "text", StringComparison.Ordinal))
                {
                    return DirectApiTextBodyRead.Invalid;
                }

                if (property.Value.ValueKind != JsonValueKind.String)
                    return DirectApiTextBodyRead.Invalid;

                text = property.Value.GetString();
            }

            return text is { Length: > 0 }
                ? new DirectApiTextBodyRead(true, text)
                : DirectApiTextBodyRead.Invalid;
        }
        catch (JsonException)
        {
            return DirectApiTextBodyRead.Invalid;
        }
    }

    /// <summary>
    /// Writes the canonical request object in fixed property order. The
    /// payload is built from route values and the already validated text, so
    /// callers cannot supply a second target or a trusted fingerprint.
    /// </summary>
    public static string LaunchFingerprint(string projectId, string agentId, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(text);

        var bytes = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", FingerprintVersion);
            writer.WriteString("command", LaunchCommand);
            writer.WriteString("projectId", projectId);
            writer.WriteString("agentId", agentId);
            writer.WritePropertyName("body");
            writer.WriteStartObject();
            writer.WriteString("text", text);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Convert.ToHexString(SHA256.HashData(bytes.WrittenSpan)).ToLowerInvariant();
    }

    public static string DerivedLaunchCoordinatorKey(
        string projectId,
        string agentId,
        string publicKey) =>
        AgentLaunchCoordinatorCodec.StableToken(
            $"direct-api|launch|{projectId}|{agentId}|{publicKey}");
}

public enum IdempotencyKeyDisposition
{
    Required,
    Invalid,
    Valid,
}

public readonly record struct IdempotencyKeyValidation(
    IdempotencyKeyDisposition Disposition,
    string? Value = null)
{
    public static IdempotencyKeyValidation Required { get; } =
        new(IdempotencyKeyDisposition.Required);

    public static IdempotencyKeyValidation Invalid { get; } =
        new(IdempotencyKeyDisposition.Invalid);

    public bool IsValid => Disposition == IdempotencyKeyDisposition.Valid;
}

public readonly record struct DirectApiTextBodyRead(bool IsValid, string? Text)
{
    public static DirectApiTextBodyRead Invalid { get; } = new(false, null);
}
