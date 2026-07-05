using Microsoft.Extensions.Configuration;

namespace Mohist.Server.Logging;

/// <summary>
/// Default <see cref="ILogPathResolver"/>: honors a <c>Mohist:LogsPath</c>
/// configuration override (test fixtures and the per-run alternate host
/// both rely on this), otherwise returns
/// <c>$HOME/.mohist/logs</c> — identical to the previous inline
/// computation duplicated in <c>LogsRoutes</c> and
/// <c>SystemInfoService</c>.
/// </summary>
public sealed class LogPathResolver : ILogPathResolver
{
    public const string ConfigurationKey = "Mohist:LogsPath";

    public const string HomeEnvironmentVariable = "HOME";

    private readonly IConfiguration _configuration;
    private readonly IEnvironmentVariableProvider _environment;

    public LogPathResolver(IConfiguration configuration, IEnvironmentVariableProvider environment)
    {
        _configuration = configuration;
        _environment = environment;
    }

    public string Resolve()
    {
        var overridePath = _configuration[ConfigurationKey];
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return overridePath;
        }

        var home = _environment.GetEnvironmentVariable(HomeEnvironmentVariable)
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".mohist", "logs");
    }
}
