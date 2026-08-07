namespace Mohist.Cli;

/// <summary>
/// Resolves the credential the local CLI presents to the server, in
/// order: <c>MOHIST_TOKEN</c> (scripts/CI/PAT, may target remote
/// servers), then the admin credential — <c>MOHIST_ADMIN_TOKEN</c> as a
/// value, <c>MOHIST_ADMIN_TOKEN_PATH</c> pointing at the file, or the
/// default <c>~/.mohist/admin-token</c> the server bootstrap generated
/// (docs/auth.md "本机 mo"). The admin credential is machine-local and
/// never leaves the machine. Resolution failure — no credential at all —
/// yields null so callers pass the request through unauthenticated.
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

    public CliCredentialProvider(
        IFileSystem fileSystem,
        IEnvironmentVariableProvider environment)
    {
        _fileSystem = fileSystem;
        _environment = environment;
    }

    public async Task<CliCredential?> TryResolveAsync()
    {
        var configured = _environment.GetEnvironmentVariable(TokenEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = _environment.GetEnvironmentVariable(AdminTokenEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(configured))
            {
                var adminToken = configured.Trim();
                RequireMinimumLength(adminToken);
                return new CliCredential(adminToken, MachineLocal: true);
            }

            var path = ResolveAdminTokenPath();
            if (!_fileSystem.Exists(path))
                return null;
            try
            {
                var fileToken = (await _fileSystem.ReadAllTextAsync(path).ConfigureAwait(false)).Trim();
                RequireMinimumLength(fileToken);
                return new CliCredential(fileToken, MachineLocal: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    $"Mohist credential could not be read from '{path}': {ex.Message}", ex);
            }
        }

        var token = configured.Trim();
        RequireMinimumLength(token);
        return new CliCredential(token, MachineLocal: false);
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

/// <summary>
/// A resolved CLI credential. <see cref="MachineLocal"/> distinguishes
/// the machine-local admin credential (only attach to loopback
/// destinations) from an explicitly supplied <c>MOHIST_TOKEN</c> (safe
/// for remote servers).
/// </summary>
internal sealed record CliCredential(string Token, bool MachineLocal);
