using System.Security.Cryptography;
using System.Text;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Infrastructure.Security;

public sealed class OperatorCredential : ISingletonService
{
    public const string HeaderName = "X-Mohist-Operator-Token";
    public const string TokenEnvironmentVariable = "MOHIST_OPERATOR_TOKEN";
    public const string TokenPathEnvironmentVariable = "MOHIST_OPERATOR_TOKEN_PATH";

    private const int MinimumTokenLength = 32;
    private readonly byte[] _token;

    public OperatorCredential(
        IConfiguration configuration,
        IEnvironmentVariableProvider environment)
        : this(
            configuration,
            environment,
            PhysicalOperatorCredentialStore.Instance)
    {
    }

    internal OperatorCredential(
        IConfiguration configuration,
        IEnvironmentVariableProvider environment,
        IOperatorCredentialStore store)
    {
        var environmentToken = environment.GetEnvironmentVariable(TokenEnvironmentVariable);
        var configured = string.IsNullOrWhiteSpace(environmentToken)
            ? configuration["Mohist:OperatorToken"]
            : environmentToken;
        string token;
        if (string.IsNullOrWhiteSpace(configured))
        {
            var path = ResolvePath(configuration, environment);
            token = path.IsExplicit
                ? store.ReadExplicit(path.Value)
                : store.LoadOrCreateDefault(path.Value);
        }
        else
        {
            token = configured;
        }

        token = token.Trim();
        if (token.Length < MinimumTokenLength)
            throw new InvalidOperationException(
                $"Mohist operator token must contain at least {MinimumTokenLength} characters.");

        _token = Encoding.UTF8.GetBytes(token);
    }

    public bool Authorizes(IHeaderDictionary headers)
    {
        if (!headers.TryGetValue(HeaderName, out var values) || values.Count != 1)
            return false;

        var supplied = values[0];
        if (string.IsNullOrEmpty(supplied))
            return false;

        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        return suppliedBytes.Length == _token.Length
            && CryptographicOperations.FixedTimeEquals(suppliedBytes, _token);
    }

    internal static OperatorCredentialPath ResolvePath(
        IConfiguration configuration,
        IEnvironmentVariableProvider environment)
    {
        var environmentPath = environment.GetEnvironmentVariable(TokenPathEnvironmentVariable);
        var configured = string.IsNullOrWhiteSpace(environmentPath)
            ? configuration["Mohist:OperatorTokenPath"]
            : environmentPath;
        if (!string.IsNullOrWhiteSpace(configured))
            return new OperatorCredentialPath(Path.GetFullPath(configured), IsExplicit: true);

        var home = environment.GetEnvironmentVariable(MohistServiceRegistration.HomeEnvironmentVariable)
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new OperatorCredentialPath(
            Path.Combine(home, ".mohist", "operator-token"),
            IsExplicit: false);
    }

    internal interface IOperatorCredentialStore
    {
        string LoadOrCreateDefault(string path);

        string ReadExplicit(string path);
    }

    private sealed class PhysicalOperatorCredentialStore
        : IOperatorCredentialStore
    {
        public static PhysicalOperatorCredentialStore Instance
            { get; } = new();

        public string LoadOrCreateDefault(string path)
        {
            if (File.Exists(path))
                return ReadAndSecure(path);

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var token = Convert.ToBase64String(
                    RandomNumberGenerator.GetBytes(32))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');

            try
            {
                var options = new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.Read,
                    Options = FileOptions.WriteThrough,
                };
                if (!OperatingSystem.IsWindows())
                {
                    options.UnixCreateMode =
                        UnixFileMode.UserRead |
                        UnixFileMode.UserWrite;
                }

                using var stream = new FileStream(path, options);
                using var writer = new StreamWriter(
                    stream,
                    new UTF8Encoding(false));
                writer.Write(token);
                writer.Flush();
                stream.Flush(flushToDisk: true);
                return token;
            }
            catch (IOException) when (File.Exists(path))
            {
                return ReadAndSecure(path);
            }
        }

        private static string ReadAndSecure(string path)
        {
            if ((File.GetAttributes(path) &
                 FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    $"Mohist operator credential path '{path}' " +
                    "must not be a symbolic link.");
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead |
                    UnixFileMode.UserWrite);
            }

            return File.ReadAllText(path, Encoding.UTF8);
        }

        public string ReadExplicit(string path)
        {
            try
            {
                return File.ReadAllText(path, Encoding.UTF8);
            }
            catch (Exception ex)
                when (ex is IOException or
                    UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    $"Mohist operator credential could not " +
                    $"be read from '{path}': {ex.Message}",
                    ex);
            }
        }
    }

    internal sealed record OperatorCredentialPath(string Value, bool IsExplicit);
}
