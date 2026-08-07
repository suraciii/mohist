using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Mohist.Server.Auth.Domain;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Auth.Identity;

/// <summary>
/// Loads the two deployment-level file credentials at startup and
/// resolves a presented token against them with constant-time
/// comparison. The admin credential (admin-token) derives the single
/// admin Principal; the service credential (operator-token) derives the
/// service Principal and keeps the legacy env/config names so deployed
/// environments need no rotation.
/// </summary>
public sealed class FileCredentialLoader : ISingletonService
{
    public const string AdminTokenEnvironmentVariable = "MOHIST_ADMIN_TOKEN";
    public const string AdminTokenPathEnvironmentVariable = "MOHIST_ADMIN_TOKEN_PATH";
    public const string ServiceTokenEnvironmentVariable = "MOHIST_OPERATOR_TOKEN";
    public const string ServiceTokenPathEnvironmentVariable = "MOHIST_OPERATOR_TOKEN_PATH";

    private const string AdminTokenConfigKey = "Mohist:AdminToken";
    private const string AdminTokenPathConfigKey = "Mohist:AdminTokenPath";
    private const string ServiceTokenConfigKey = "Mohist:OperatorToken";
    private const string ServiceTokenPathConfigKey = "Mohist:OperatorTokenPath";
    private const string DefaultTokenDirectoryName = ".mohist";
    private const string AdminTokenFileName = "admin-token";
    private const string ServiceTokenFileName = "operator-token";
    private const int MinimumTokenLength = 32;

    private readonly byte[] _adminToken;
    private readonly byte[] _serviceToken;
    private readonly MohistPrincipal _adminPrincipal = new(
        MohistPrincipal.AdminPrincipalId,
        PrincipalKind.Admin,
        "admin",
        [Scope.Operator]);
    private readonly MohistPrincipal _servicePrincipal = new(
        MohistPrincipal.ServicePrincipalId,
        PrincipalKind.Service,
        "operator",
        [Scope.Operator]);

    public FileCredentialLoader(
        IConfiguration configuration,
        IEnvironmentVariableProvider environment,
        IFileCredentialStore store)
    {
        _adminToken = RequireMinimumLength(
            LoadToken(configuration, environment, store,
                AdminTokenEnvironmentVariable, AdminTokenPathEnvironmentVariable,
                AdminTokenConfigKey, AdminTokenPathConfigKey, AdminTokenFileName),
            "admin");
        _serviceToken = RequireMinimumLength(
            LoadToken(configuration, environment, store,
                ServiceTokenEnvironmentVariable, ServiceTokenPathEnvironmentVariable,
                ServiceTokenConfigKey, ServiceTokenPathConfigKey, ServiceTokenFileName),
            "service");
    }

    public MohistPrincipal? TryResolve(string token)
    {
        if (Matches(_adminToken, token))
            return _adminPrincipal;
        if (Matches(_serviceToken, token))
            return _servicePrincipal;
        return null;
    }

    private static bool Matches(byte[] expected, string supplied)
    {
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        return suppliedBytes.Length == expected.Length
            && CryptographicOperations.FixedTimeEquals(suppliedBytes, expected);
    }

    private static string LoadToken(
        IConfiguration configuration,
        IEnvironmentVariableProvider environment,
        IFileCredentialStore store,
        string tokenEnvironmentVariable,
        string pathEnvironmentVariable,
        string configKey,
        string pathConfigKey,
        string defaultFileName)
    {
        var environmentToken = environment.GetEnvironmentVariable(tokenEnvironmentVariable);
        var configured = string.IsNullOrWhiteSpace(environmentToken)
            ? configuration[configKey]
            : environmentToken;
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.Trim();

        var path = ResolvePath(configuration, environment, pathEnvironmentVariable, pathConfigKey, defaultFileName);
        return path.IsExplicit
            ? store.ReadExplicit(path.Value).Trim()
            : store.LoadOrCreateDefault(path.Value).Trim();
    }

    private static FileCredentialPath ResolvePath(
        IConfiguration configuration,
        IEnvironmentVariableProvider environment,
        string pathEnvironmentVariable,
        string pathConfigKey,
        string defaultFileName)
    {
        var environmentPath = environment.GetEnvironmentVariable(pathEnvironmentVariable);
        var configured = string.IsNullOrWhiteSpace(environmentPath)
            ? configuration[pathConfigKey]
            : environmentPath;
        if (!string.IsNullOrWhiteSpace(configured))
            return new FileCredentialPath(Path.GetFullPath(configured), IsExplicit: true);

        var home = environment.GetEnvironmentVariable(MohistServiceRegistration.HomeEnvironmentVariable)
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new FileCredentialPath(
            Path.Combine(home, DefaultTokenDirectoryName, defaultFileName),
            IsExplicit: false);
    }

    private static byte[] RequireMinimumLength(string token, string name)
    {
        if (token.Length < MinimumTokenLength)
        {
            throw new InvalidOperationException(
                $"Mohist {name} credential must contain at least {MinimumTokenLength} characters.");
        }

        return Encoding.UTF8.GetBytes(token);
    }

    internal sealed record FileCredentialPath(string Value, bool IsExplicit);
}
