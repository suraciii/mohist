namespace Mohist.Cli;

/// <summary>
/// Resolves the credential the local CLI presents to the server, in
/// order: <c>MOHIST_TOKEN</c>
/// (scripts/CI/PAT, may target remote servers), then the device-login
/// session in <c>~/.mohist/credentials.json</c> matched by server, then
/// the machine-local admin credential — <c>MOHIST_ADMIN_TOKEN</c> as a
/// value, <c>MOHIST_ADMIN_TOKEN_PATH</c> pointing at the file, or the
/// default <c>~/.mohist/admin-token</c> the server bootstrap generated.
/// The admin credential is machine-local and never leaves the machine.
/// Resolution failure — no credential at all — yields null so callers
/// pass the request through unauthenticated.
/// </summary>
internal sealed class CliCredentialProvider
{
    public const string TokenEnvironmentVariable = "MOHIST_TOKEN";
    public const string AdminTokenEnvironmentVariable = "MOHIST_ADMIN_TOKEN";
    public const string AdminTokenPathEnvironmentVariable = "MOHIST_ADMIN_TOKEN_PATH";
    internal const string DefaultTokenDirectoryName = ".mohist";
    internal const string DefaultTokenFileName = "admin-token";

    private const int MinimumTokenLength = 32;
    private readonly IFileSystem _fileSystem;
    private readonly IEnvironmentVariableProvider _environment;
    private readonly Func<string> _getUserHome;

    public CliCredentialProvider(
        IFileSystem fileSystem,
        IEnvironmentVariableProvider environment,
        Func<string>? getUserHome = null)
    {
        _fileSystem = fileSystem;
        _environment = environment;
        _getUserHome = getUserHome ?? (() =>
            _environment.GetEnvironmentVariable("HOME")
            ?? (fileSystem is RealFileSystem
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : "/mohist-tests/user"));
    }

    public async Task<CliCredential?> TryResolveAsync(Uri? destination)
    {
        var configured = _environment.GetEnvironmentVariable(TokenEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            var stored = destination is null
                ? null
                : await TryResolveStoredAsync(destination).ConfigureAwait(false);
            if (stored is not null)
                return stored;

            var admin = _environment.GetEnvironmentVariable(AdminTokenEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(admin))
            {
                var adminToken = admin.Trim();
                RequireMinimumLength(adminToken);
                return new CliCredential(adminToken, MachineLocal: true, Source: CliCredentialSource.AdminEnvironment);
            }

            var path = ResolveAdminTokenPath();
            if (!_fileSystem.Exists(path))
                return null;
            try
            {
                var fileToken = (await _fileSystem.ReadAllTextAsync(path).ConfigureAwait(false)).Trim();
                RequireMinimumLength(fileToken);
                return new CliCredential(fileToken, MachineLocal: true, Source: CliCredentialSource.AdminFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    $"Mohist credential could not be read from '{path}': {ex.Message}", ex);
            }
        }

        var token = configured.Trim();
        RequireMinimumLength(token);
        return new CliCredential(token, MachineLocal: false, Source: CliCredentialSource.EnvironmentToken);
    }

    private async Task<CliCredential?> TryResolveStoredAsync(Uri destination)
    {
        var file = new CliCredentialFile(_fileSystem, CliCredentialFile.PathFor(_getUserHome));
        // Match on the origin only: the destination is a request URI whose
        // path varies per command.
        var entry = await file.FindAsync(destination.GetLeftPart(UriPartial.Authority)).ConfigureAwait(false);
        return entry is null
            ? null
            : new CliCredential(
                entry.AccessToken,
                MachineLocal: false,
                Source: CliCredentialSource.CredentialFile,
                Stored: entry);
    }

    private string ResolveAdminTokenPath()
    {
        var configuredPath = _environment.GetEnvironmentVariable(AdminTokenPathEnvironmentVariable);
        return string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(HomeDirectory(), DefaultTokenDirectoryName, DefaultTokenFileName)
            : Path.GetFullPath(configuredPath);
    }

    private string HomeDirectory() =>
        _environment.GetEnvironmentVariable("HOME")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static void RequireMinimumLength(string token)
    {
        if (token.Length < MinimumTokenLength)
        {
            throw new InvalidOperationException(
                $"Mohist credential must contain at least {MinimumTokenLength} characters.");
        }
    }
}

internal enum CliCredentialSource
{
    EnvironmentToken,
    CredentialFile,
    AdminEnvironment,
    AdminFile,
}

/// <summary>
/// A resolved CLI credential. <see cref="MachineLocal"/> distinguishes
/// the machine-local admin credential (only attach to loopback
/// destinations) from an explicitly supplied <c>MOHIST_TOKEN</c> (safe
/// for remote servers). <see cref="Stored"/> carries the credentials.json
/// entry so the handler can roll the session forward on a 401.
/// </summary>
internal sealed record CliCredential(
    string Token,
    bool MachineLocal,
    CliCredentialSource Source = CliCredentialSource.AdminFile,
    StoredCliCredential? Stored = null);
