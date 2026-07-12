namespace Mohist.Cli;

internal sealed class OperatorCredentialProvider
{
    public const string HeaderName = "X-Mohist-Operator-Token";
    public const string TokenEnvironmentVariable = "MOHIST_OPERATOR_TOKEN";
    public const string TokenPathEnvironmentVariable = "MOHIST_OPERATOR_TOKEN_PATH";

    private const int MinimumTokenLength = 32;
    private readonly IFileSystem _fileSystem;
    private readonly IEnvironmentVariableProvider _environment;

    public OperatorCredentialProvider(
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
                $"Mohist operator token must contain at least {MinimumTokenLength} characters.");
        return token;
    }

    private string ResolvePath()
    {
        var configured = _environment.GetEnvironmentVariable(TokenPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured);

        var home = _environment.GetEnvironmentVariable("HOME")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".mohist", "operator-token");
    }

    private async Task<string> ReadFileAsync(string path)
    {
        if (!_fileSystem.Exists(path))
            throw new InvalidOperationException(
                $"Mohist operator credential was not found at '{path}'. Start the server or set {TokenEnvironmentVariable}.");

        try
        {
            return await _fileSystem.ReadAllTextAsync(path).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Mohist operator credential could not be read from '{path}': {ex.Message}", ex);
        }
    }
}
