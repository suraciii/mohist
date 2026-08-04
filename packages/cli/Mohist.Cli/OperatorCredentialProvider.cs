using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal sealed class OperatorCredentialProvider
{
    public const string HeaderName = "X-Mohist-Operator-Token";
    public const string TokenEnvironmentVariable = "MOHIST_OPERATOR_TOKEN";
    public const string TokenPathEnvironmentVariable = "MOHIST_OPERATOR_TOKEN_PATH";
    internal const string DefaultTokenDirectoryName = ".mohist";
    internal const string DefaultTokenFileName = "operator-token";

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
        var userConfig = string.IsNullOrWhiteSpace(configured)
            ? ReadUserConfig()
            : UserConfig.Empty;
        if (string.IsNullOrWhiteSpace(configured))
            configured = userConfig.Token;
        var token = string.IsNullOrWhiteSpace(configured)
            ? await ReadFileAsync(ResolvePath(userConfig.TokenPath)).ConfigureAwait(false)
            : configured;

        token = token.Trim();
        if (token.Length < MinimumTokenLength)
            throw new InvalidOperationException(
                $"Mohist operator token must contain at least {MinimumTokenLength} characters.");
        return token;
    }

    private string ResolvePath(string? configuredPath)
    {
        var environmentPath = _environment.GetEnvironmentVariable(TokenPathEnvironmentVariable);
        var configured = string.IsNullOrWhiteSpace(environmentPath)
            ? configuredPath
            : environmentPath;
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured);

        return Path.Combine(HomeDirectory(), DefaultTokenDirectoryName, DefaultTokenFileName);
    }

    private UserConfig ReadUserConfig()
    {
        var path = Path.Combine(HomeDirectory(), ".mohist", "config.jsonc");
        if (!_fileSystem.Exists(path))
            return UserConfig.Empty;

        try
        {
            var root = JsonNode.Parse(
                _fileSystem.ReadAllText(path),
                documentOptions: new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });
            return new UserConfig(
                root?["Mohist"]?["OperatorToken"]?.GetValue<string>(),
                root?["Mohist"]?["OperatorTokenPath"]?.GetValue<string>());
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"Mohist operator credential configuration could not be read from '{path}': {ex.Message}", ex);
        }
    }

    private string HomeDirectory()
    {
        return _environment.GetEnvironmentVariable("HOME")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
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

    private sealed record UserConfig(string? Token, string? TokenPath)
    {
        public static readonly UserConfig Empty = new(null, null);
    }
}
