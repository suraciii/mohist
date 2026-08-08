using System.Text.Json;

namespace Mohist.Cli;

/// <summary>
/// The CLI's local session store, <c>~/.mohist/credentials.json</c>
/// (0600): one entry per server, holding the device-flow access +
/// refresh pair. Only the refresh token can move the session forward,
/// so the file is written user-only and entries are matched by server
/// URL when a command targets a remote Mohist.
/// </summary>
internal sealed class CliCredentialFile
{
    public const string FileName = "credentials.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly IFileSystem _fileSystem;
    private readonly string _path;

    public CliCredentialFile(IFileSystem fileSystem, string path)
    {
        _fileSystem = fileSystem;
        _path = path;
    }

    public static string PathFor(Func<string> getUserHome) =>
        System.IO.Path.Combine(getUserHome(), ".mohist", FileName);

    public static string NormalizeServer(string server) =>
        (server ?? string.Empty).TrimEnd('/');

    public async Task<StoredCliCredential?> FindAsync(string server)
    {
        if (!_fileSystem.Exists(_path))
            return null;

        try
        {
            var document = await _fileSystem.ReadAllTextAsync(_path).ConfigureAwait(false);
            var file = JsonSerializer.Deserialize<CredentialFile>(document, JsonOptions);
            var normalized = NormalizeServer(server);
            return file?.Servers?.FirstOrDefault(entry =>
                string.Equals(NormalizeServer(entry.Server), normalized, StringComparison.Ordinal));
        }
        catch (JsonException)
        {
            // A corrupt local file must not break every command; it reads
            // as absent and the next successful login rewrites it.
            return null;
        }
    }

    /// <summary>
    /// Upserts the entry for the server and writes the file user-only.
    /// </summary>
    public Task SaveAsync(StoredCliCredential credential)
    {
        var file = ReadOrDefault();
        var normalized = NormalizeServer(credential.Server);
        file.Servers.RemoveAll(entry =>
            string.Equals(NormalizeServer(entry.Server), normalized, StringComparison.Ordinal));
        file.Servers.Add(credential);
        _fileSystem.WriteAllTextUserOnly(_path, JsonSerializer.Serialize(file, JsonOptions));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Removes the entry for the server; the file itself stays (other
    /// servers may still have sessions).
    /// </summary>
    public Task RemoveAsync(string server)
    {
        var file = ReadOrDefault();
        var normalized = NormalizeServer(server);
        file.Servers.RemoveAll(entry =>
            string.Equals(NormalizeServer(entry.Server), normalized, StringComparison.Ordinal));
        _fileSystem.WriteAllTextUserOnly(_path, JsonSerializer.Serialize(file, JsonOptions));
        return Task.CompletedTask;
    }

    private CredentialFile ReadOrDefault()
    {
        if (!_fileSystem.Exists(_path))
            return new CredentialFile();

        try
        {
            var document = _fileSystem.ReadAllText(_path);
            return JsonSerializer.Deserialize<CredentialFile>(document, JsonOptions) ?? new CredentialFile();
        }
        catch (JsonException)
        {
            return new CredentialFile();
        }
    }

    private sealed class CredentialFile
    {
        public List<StoredCliCredential> Servers { get; set; } = [];
    }
}

internal sealed record StoredCliCredential(
    string Server,
    string AccessToken,
    string RefreshToken,
    DateTimeOffset AccessExpiresAt,
    DateTimeOffset RefreshExpiresAt);
