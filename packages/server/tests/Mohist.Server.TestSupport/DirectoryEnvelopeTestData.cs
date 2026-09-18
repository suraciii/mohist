using System.Text;
using System.Text.Json;

namespace Mohist.Server.TestSupport;

/// <summary>
/// Builds the newline-delimited directory envelope used by the artifact
/// upload wire format: a <c>{"kind":"directory"}</c> header followed by
/// one JSON value per contained file.
/// </summary>
public static class DirectoryEnvelopeTestData
{
    private const string Header = "{\"kind\":\"directory\"}\n";

    public static byte[] Create(params DirectoryEnvelopeTestFile[] files) =>
        Encoding.UTF8.GetBytes(Serialize(files));

    public static string Serialize(params DirectoryEnvelopeTestFile[] files)
    {
        var builder = new StringBuilder();
        builder.Append(Header);
        foreach (var file in files)
        {
            builder.Append(SerializeFile(file));
            builder.Append('\n');
        }
        return builder.ToString();
    }

    /// <summary>
    /// Encoded NDJSON byte length contributed by each file, in the same
    /// order passed to <see cref="Create"/>. A read-ahead probe uses it to
    /// subtract the entries already yielded from the absolute bytes read.
    /// </summary>
    public static long[] EntryEncodedLengths(params DirectoryEnvelopeTestFile[] files) =>
        files.Select(file => (long)Encoding.UTF8.GetByteCount(SerializeFile(file)) + 1).ToArray();

    private static string SerializeFile(DirectoryEnvelopeTestFile file) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["path"] = file.Path,
            ["size"] = file.Size,
            ["contentHash"] = file.ContentHash,
            ["contentType"] = file.ContentType,
            ["data"] = Convert.ToBase64String(file.Content),
        });
}

public sealed record DirectoryEnvelopeTestFile(
    string Path,
    byte[] Content,
    string? ContentType = null,
    string? ContentHash = null,
    long? Size = null);
