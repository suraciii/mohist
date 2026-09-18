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
    public static byte[] Create(params DirectoryEnvelopeTestFile[] files) =>
        Encoding.UTF8.GetBytes(Serialize(files));

    public static string Serialize(params DirectoryEnvelopeTestFile[] files)
    {
        var builder = new StringBuilder();
        builder.Append("{\"kind\":\"directory\"}\n");
        foreach (var file in files)
        {
            builder.Append(JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["path"] = file.Path,
                ["size"] = file.Size,
                ["contentHash"] = file.ContentHash,
                ["contentType"] = file.ContentType,
                ["data"] = Convert.ToBase64String(file.Content),
            }));
            builder.Append('\n');
        }
        return builder.ToString();
    }
}

public sealed record DirectoryEnvelopeTestFile(
    string Path,
    byte[] Content,
    string? ContentType = null,
    string? ContentHash = null,
    long? Size = null);
