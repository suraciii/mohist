namespace Mohist.Cli;

/// <summary>
/// Resolves the credential the local CLI presents to the server:
/// <c>MOHIST_TOKEN</c> wins (scripts/CI/PAT), otherwise the admin-token
/// file the server bootstrap generated (docs/auth.md "本机 mo").
/// </summary>
internal sealed class CliCredentialProvider
{
    public const string TokenEnvironmentVariable = "MOHIST_TOKEN";
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

    public async Task<string> GetAsync()
    {
        var configured = _environment.GetEnvironmentVariable(TokenEnvironmentVariable);
        var token = string.IsNullOrWhiteSpace(configured)
            ? await ReadFileAsync(ResolvePath()).ConfigureAwait(false)
            : configured;

        token = token.Trim();
        if (token.Length < MinimumTokenLength)
            throw new InvalidOperationException(
                $"Mohist credential must contain at least {MinimumTokenLength} characters.");
        return token;
    }

    private string ResolvePath() =>
        Path.Combine(HomeDirectory(), DefaultTokenDirectoryName, DefaultTokenFileName);

    private string HomeDirectory() =>
        _environment.GetEnvironmentVariable("HOME")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private async Task<string> ReadFileAsync(string path)
    {
        if (!_fileSystem.Exists(path))
            throw new InvalidOperationException(
                $"Mohist credential was not found at '{path}'. Start the server or set {TokenEnvironmentVariable}.");

        try
        {
            return await _fileSystem.ReadAllTextAsync(path).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Mohist credential could not be read from '{path}': {ex.Message}", ex);
        }
    }
}
