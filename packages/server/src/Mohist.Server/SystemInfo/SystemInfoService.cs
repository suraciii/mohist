using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Logging;

namespace Mohist.Server.SystemInfo;

public sealed class SystemInfoService : ISingletonService
{
    public const string HomeEnvironmentVariable = "HOME";

    private readonly IRuntimeBuildInfo _runtimeBuildInfo;
    private readonly SystemdInstallDetector _installDetector;
    private readonly IGitSourceInspector _gitInspector;
    private readonly IServiceStatusChecker _serviceStatusChecker;
    private readonly IConfiguration _configuration;
    private readonly IEnvironmentVariableProvider _environment;
    private readonly ILogPathResolver _logPathResolver;
    private readonly ILogger<SystemInfoService> _logger;

    public SystemInfoService(
        IRuntimeBuildInfo runtimeBuildInfo,
        SystemdInstallDetector installDetector,
        IGitSourceInspector gitInspector,
        IServiceStatusChecker serviceStatusChecker,
        IConfiguration configuration,
        IEnvironmentVariableProvider environment,
        ILogPathResolver logPathResolver,
        ILogger<SystemInfoService> logger)
    {
        _runtimeBuildInfo = runtimeBuildInfo;
        _installDetector = installDetector;
        _gitInspector = gitInspector;
        _serviceStatusChecker = serviceStatusChecker;
        _configuration = configuration;
        _environment = environment;
        _logPathResolver = logPathResolver;
        _logger = logger;
    }

    public async Task<SystemInfoResponse> GetSystemInfoAsync()
    {
        var install = _installDetector.Detect();
        var source = await InspectSourceAsync(install.SourcePath);
        var services = await CheckServicesAsync(install);
        var update = ComputeUpdateStatus(install, source);
        var paths = ResolvePaths();

        return new SystemInfoResponse(
            Running: new RunningInfo(
                _runtimeBuildInfo.Version,
                _runtimeBuildInfo.GitHash,
                _runtimeBuildInfo.StartedAt,
                _runtimeBuildInfo.TreeHash,
                _runtimeBuildInfo.ArtifactDigest,
                _runtimeBuildInfo.ReleaseId,
                _runtimeBuildInfo.Generation,
                _runtimeBuildInfo.Component,
                _runtimeBuildInfo.SourceRevision),
            Source: source,
            Install: new InstallInfo(
                install.Mode,
                install.ServiceManager,
                install.ServerUnit,
                install.RunnerUnit,
                install.Reason),
            Update: update,
            Services: services,
            Paths: paths);
    }

    private async Task<SourceInfo> InspectSourceAsync(string? repoPath)
    {
        if (string.IsNullOrWhiteSpace(repoPath))
            return new SourceInfo(null, null, null, false);

        try
        {
            var state = await _gitInspector.InspectAsync(repoPath);
            return new SourceInfo(state.Path, state.Branch, state.Head, state.Dirty);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to inspect source repository at {RepoPath}", repoPath);
            return new SourceInfo(repoPath, null, null, false);
        }
    }

    private async Task<ServiceInfo> CheckServicesAsync(InstallDetectionResult install)
    {
        if (install.Mode != "local-source")
            return new ServiceInfo(null, null);

        var serverStatus = await _serviceStatusChecker.GetStatusAsync(install.ServerUnit);
        var runnerStatus = await _serviceStatusChecker.GetStatusAsync(install.RunnerUnit);

        return new ServiceInfo(serverStatus, runnerStatus);
    }

    private UpdateInfo ComputeUpdateStatus(InstallDetectionResult install, SourceInfo source)
    {
        if (install.Mode != "local-source")
        {
            return new UpdateInfo(
                "unsupported",
                false,
                "Web update is unsupported for the detected deployment");
        }

        var enabled = IsUpdateEnabled(install);

        if (string.IsNullOrWhiteSpace(_runtimeBuildInfo.GitHash) || string.IsNullOrWhiteSpace(source.Head))
        {
            var missing = string.IsNullOrWhiteSpace(_runtimeBuildInfo.GitHash)
                ? "running git hash"
                : "source HEAD";
            return new UpdateInfo(
                "unknown",
                false,
                $"Cannot determine update status: {missing} is unavailable");
        }

        if (source.Dirty)
        {
            return new UpdateInfo(
                "dirty-source",
                false,
                "Source tree has uncommitted changes");
        }

        if (_runtimeBuildInfo.GitHash == source.Head)
        {
            return new UpdateInfo(
                "up-to-date",
                false,
                "Running server is up to date with source");
        }

        if (!enabled)
        {
            return new UpdateInfo(
                "unsupported",
                false,
                "System update is disabled by configuration");
        }

        return new UpdateInfo(
            "update-available",
            true,
            "A newer source version is available");
    }

    private bool IsUpdateEnabled(InstallDetectionResult install)
    {
        var configured = _configuration["Mohist:SystemUpdate:Enabled"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return bool.TryParse(configured, out var explicitValue) && explicitValue;
        }

        // Default: enabled only for proven safe local-source installs
        return install.Mode == "local-source"
            && !string.IsNullOrWhiteSpace(install.SourcePath)
            && !string.IsNullOrWhiteSpace(install.ServerUnit);
    }

    private SystemPaths ResolvePaths()
    {
        var home = _environment.GetEnvironmentVariable(HomeEnvironmentVariable)
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var dataDir = Path.Combine(home, ".mohist");
        var dbPath = _configuration["Mohist:DbPath"];
        if (string.IsNullOrWhiteSpace(dbPath))
        {
            dbPath = Path.Combine(dataDir, "mohist.db");
        }

        return new SystemPaths(
            Db: dbPath,
            Config: Path.Combine(dataDir, "config.jsonc"),
            Logs: _logPathResolver.Resolve(),
            Opencode: Path.Combine(home, ".config", "opencode"));
    }
}
