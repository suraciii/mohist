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
    {
        var environmentToken = environment.GetEnvironmentVariable(TokenEnvironmentVariable);
        var configured = string.IsNullOrWhiteSpace(environmentToken)
            ? configuration["Mohist:OperatorToken"]
            : environmentToken;
        var token = string.IsNullOrWhiteSpace(configured)
            ? LoadOrCreate(ResolvePath(configuration, environment))
            : configured;

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

    internal static string ResolvePath(
        IConfiguration configuration,
        IEnvironmentVariableProvider environment)
    {
        var environmentPath = environment.GetEnvironmentVariable(TokenPathEnvironmentVariable);
        var configured = string.IsNullOrWhiteSpace(environmentPath)
            ? configuration["Mohist:OperatorTokenPath"]
            : environmentPath;
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured);

        var home = environment.GetEnvironmentVariable(MohistServiceRegistration.HomeEnvironmentVariable)
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".mohist", "operator-token");
    }

    private static string LoadOrCreate(string path)
    {
        if (File.Exists(path))
            return ReadAndSecure(path);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
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
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

            using var stream = new FileStream(path, options);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
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
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException(
                $"Mohist operator credential path '{path}' must not be a symbolic link.");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return File.ReadAllText(path, Encoding.UTF8);
    }
}
