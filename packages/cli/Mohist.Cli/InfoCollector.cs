using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal sealed partial class InfoCollector
{
    internal const string ServerUnit = SystemdUnitParser.ServerUnit;
    internal const string RunnerUnit = SystemdUnitParser.RunnerUnit;

    internal const string ServerOk = "server ok";
    internal const string ServerUnreachable = "server unreachable";
    internal const string PlatformNoticeMessage =
        "service management not supported on this platform; showing process info only";
    internal const string TimeoutSentinel = "<timeout>";

    private static readonly TimeSpan CollectorTimeout = TimeSpan.FromSeconds(2);

    private const string ShowProperties = SystemdUnitParser.ShowProperties;

    private readonly IFileSystem _fileSystem;
    private readonly ICommandExecutor _commandExecutor;
    private readonly IEnvironmentVariableProvider _environment;
    private readonly MohistCliApi _api;
    private readonly SkillAssetService? _skillAssetService;
    private readonly Func<string?> _getServerBaseAddress;
    private readonly Func<string?> _getRunnerServerUrlOverride;
    private readonly Func<bool>? _isSystemdAvailableOverride;

    internal sealed record Options(
        Func<bool>? IsSystemdAvailable = null,
        SkillAssetService? SkillAssetService = null);

    public InfoCollector(
        IFileSystem fileSystem,
        ICommandExecutor commandExecutor,
        MohistCliApi api,
        IEnvironmentVariableProvider? environment = null,
        SkillAssetService? skillAssetService = null)
        : this(fileSystem, commandExecutor, environment ?? SystemEnvironmentVariableProvider.Instance, api, new Options(SkillAssetService: skillAssetService))
    {
    }

    internal InfoCollector(
        IFileSystem fileSystem,
        ICommandExecutor commandExecutor,
        MohistCliApi api,
        IEnvironmentVariableProvider? environment,
        Func<bool>? isSystemdAvailable,
        SkillAssetService? skillAssetService = null)
        : this(fileSystem, commandExecutor, environment ?? SystemEnvironmentVariableProvider.Instance, api,
            new Options(IsSystemdAvailable: isSystemdAvailable, SkillAssetService: skillAssetService))
    {
    }

    internal InfoCollector(
        IFileSystem fileSystem,
        ICommandExecutor commandExecutor,
        IEnvironmentVariableProvider environment,
        MohistCliApi api,
        Func<bool>? isSystemdAvailable)
        : this(fileSystem, commandExecutor, environment, api, new Options(IsSystemdAvailable: isSystemdAvailable))
    {
    }

    internal InfoCollector(
        IFileSystem fileSystem,
        ICommandExecutor commandExecutor,
        IEnvironmentVariableProvider environment,
        MohistCliApi api,
        Options? options = null)
    {
        options ??= new Options();
        _fileSystem = fileSystem;
        _commandExecutor = commandExecutor;
        _environment = environment;
        _api = api;
        _skillAssetService = options.SkillAssetService;
        _getServerBaseAddress = () => null;
        _getRunnerServerUrlOverride = () => null;
        _isSystemdAvailableOverride = options.IsSystemdAvailable;
    }

    public async Task<InfoResult> CollectAsync()
    {
        return await CollectAsync(verbose: false);
    }

    public async Task<InfoResult> CollectAsync(bool verbose)
    {
        bool systemdAvailable = IsSystemdAvailable();

        var cliTask = SafeAsync(GetCliAsync);
        var serverTask = SafeAsync(() => GetServiceInfoAsync(SystemdUnitParser.ServerUnit, systemdAvailable));
        var runnerTask = SafeAsync(() => GetServiceInfoAsync(SystemdUnitParser.RunnerUnit, systemdAvailable));
        var projectTask = SafeAsync(GetProjectAsync);
        var dataDirTask = SafeAsync(GetDataDirAsync);

        await Task.WhenAll(
            cliTask,
            serverTask,
            runnerTask,
            projectTask,
            dataDirTask);

        var cli = await cliTask;
        var server = await serverTask;
        var runner = await runnerTask;
        var project = await projectTask;
        var dataDir = await dataDirTask;

        string? connectivity = null;
        if (server.Status is { State: "active" }
            && runner.Status is { State: "active" }
            && systemdAvailable)
        {
            connectivity = await SafeAsync(CheckServerConnectivityAsync);
        }

        InfoVerbose? verboseInfo = null;
        if (verbose)
        {
            verboseInfo = await CollectVerboseAsync(server, runner, project, dataDir, systemdAvailable);
        }

        return new InfoResult(
            Cli: cli,
            Server: server,
            Runner: AttachConnectivity(runner, connectivity),
            Project: project,
            DataDir: dataDir,
            PlatformNotice: systemdAvailable ? null : PlatformNoticeMessage,
            Verbose: verboseInfo);
    }

    private static InfoService AttachConnectivity(InfoService runner, string? connectivity)
    {
        if (string.IsNullOrEmpty(connectivity))
            return runner;
        var status = runner.Status;
        if (status is null)
            return runner;
        if (!string.Equals(status.State, "active", StringComparison.OrdinalIgnoreCase))
            return runner;
        return runner with
        {
            Status = status with
            {
                Connectivity = connectivity,
            },
        };
    }


    private static bool IsWatchedEnvVar(string name) =>
    WatchedEnvVarNames.Contains(name, StringComparer.Ordinal);

    internal async Task<InfoCli> GetCliAsync()
    {
        try
        {
            var assembly = typeof(InfoCollector).Assembly;
            var infoVersion = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            var version = infoVersion;
            string? buildDate = null;
            if (infoVersion is not null)
            {
                var plus = infoVersion.IndexOf('+');
                if (plus > 0)
                {
                    version = infoVersion[..plus];
                    buildDate = infoVersion[(plus + 1)..];
                }
            }
            version ??= assembly.GetName().Version?.ToString();
            var binaryPath = TryGetBinaryPath(assembly);
            return await Task.FromResult(new InfoCli(version, binaryPath, buildDate));
        }
        catch
        {
            return new InfoCli(null, null, null);
        }
    }

    internal async Task<InfoService> GetServiceInfoAsync(string unitName, bool systemdAvailable)
    {
        using var cts = new CancellationTokenSource(CollectorTimeout);
        if (!systemdAvailable)
            return new InfoService(null, null);

        try
        {
            var (exit, stdout, _) = await WithTimeout(
                _commandExecutor.ExecuteAsync("systemctl", [
                    "--user",
                    "show",
                    unitName,
                    "-p",
                    SystemdUnitParser.ShowProperties,
                ], cancellationToken: cts.Token),
                cts.Token);

            if (exit != 0)
                return new InfoService(new InfoServiceStatus(SystemdUnitParser.NotInstalled, null, null, null, null), null);

            var properties = SystemdUnitParser.ParseSystemdShow(stdout);
            var status = SystemdUnitParser.BuildStatusFromProperties(properties, _fileSystem);
            var source = await BuildSourceFromPropertiesAsync(properties);
            return new InfoService(status, source);
        }
        catch
        {
            return new InfoService(new InfoServiceStatus(SystemdUnitParser.NotInstalled, null, null, null, null), null);
        }
    }

    private async Task<InfoSource?> BuildSourceFromPropertiesAsync(Dictionary<string, string> properties)
    {
        var workingDirectory = properties.TryGetValue("WorkingDirectory", out var wd) ? wd : null;
        var execStart = properties.TryGetValue("ExecStart", out var es) ? es : null;
        var fragmentPath = properties.TryGetValue("FragmentPath", out var fp) ? fp : null;

        var resolved = ResolveSourcePath(new SystemdUnitParser.SystemdUnitFields(workingDirectory, execStart));
        if (string.IsNullOrWhiteSpace(resolved))
        {
            if (!string.IsNullOrWhiteSpace(fragmentPath) && _fileSystem.Exists(fragmentPath))
            {
                var content = await _fileSystem.ReadAllTextAsync(fragmentPath);
                var unit = SystemdUnitParser.ParseSystemdUnit(content);
                resolved = ResolveSourcePath(unit);
            }
        }

        if (string.IsNullOrWhiteSpace(resolved))
            return null;

        return await InspectSourceAsync(resolved!);
    }

    internal async Task<InfoProject?> GetProjectAsync()
    {
        using var cts = new CancellationTokenSource(CollectorTimeout);
        try
        {
            var activeProjectId = await ReadActiveProjectIdAsync();
            if (string.IsNullOrWhiteSpace(activeProjectId))
                return new InfoProject(null, "<no project>", 0, 0);

            var path = $"/api/projects/{Uri.EscapeDataString(activeProjectId!)}/status";
            using var response = await _api.Http.GetAsync(path, cts.Token);
            if (!response.IsSuccessStatusCode)
                return new InfoProject(activeProjectId, null, null, null);

            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            if (stream.Length == 0)
                return new InfoProject(activeProjectId, null, null, null);
            var node = await System.Text.Json.Nodes.JsonNode.ParseAsync(stream, cancellationToken: cts.Token);
            if (node is null)
                return new InfoProject(activeProjectId, null, null, null);

            var data = node["data"];
            var name = data?["name"]?.GetValue<string>();
            int? total = data?["issues"]?.GetValue<int?>();
            int? active = data?["activeIssues"]?.GetValue<int?>();
            return new InfoProject(activeProjectId, name, total, active);
        }
        catch
        {
            return new InfoProject(null, null, null, null);
        }
    }

    internal async Task<InfoDataDir> GetDataDirAsync()
    {
        using var cts = new CancellationTokenSource(CollectorTimeout);
        try
        {
            var home = _environment.GetEnvironmentVariable("HOME")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(home))
                return new InfoDataDir(string.Empty, null);

            var path = Path.Combine(home, ".mohist");
            if (!_fileSystem.DirectoryExists(path))
                return new InfoDataDir(path, SystemdUnitParser.Unknown);

            var size = await ComputeDiskUsageAsync(path, cts.Token);
            return new InfoDataDir(path, size);
        }
        catch
        {
            return new InfoDataDir(string.Empty, null);
        }
    }

    internal async Task<string> CheckServerConnectivityAsync()
    {
        using var cts = new CancellationTokenSource(CollectorTimeout);
        try
        {
            var url = ResolveConnectivityUrl();
            using var response = await _api.Http.GetAsync(url, cts.Token);
            return response.IsSuccessStatusCode ? ServerOk : ServerUnreachable;
        }
        catch
        {
            return ServerUnreachable;
        }
    }

    private string ResolveConnectivityUrl()
    {
        var explicitUrl = _getServerBaseAddress();
        if (!string.IsNullOrWhiteSpace(explicitUrl))
            return CombineUrl(explicitUrl!, "/api/projects");

        var overrideUrl = _getRunnerServerUrlOverride();
        if (!string.IsNullOrWhiteSpace(overrideUrl))
            return CombineUrl(overrideUrl!, "/api/projects");

        return "/api/projects";
    }

    private static string CombineUrl(string baseUrl, string path)
    {
        if (string.IsNullOrEmpty(baseUrl))
            return path;
        if (baseUrl.EndsWith('/') && path.StartsWith('/'))
            return baseUrl + path[1..];
        if (!baseUrl.EndsWith('/') && !path.StartsWith('/'))
            return baseUrl + "/" + path;
        return baseUrl + path;
    }

    internal bool IsSystemdAvailable()
    {
        if (_isSystemdAvailableOverride is not null)
            return _isSystemdAvailableOverride();
        if (!OperatingSystem.IsLinux())
            return false;
        try
        {
            return _fileSystem.Exists("/run/systemd/system");
        }
        catch
        {
            return false;
        }
    }

    private async Task<InfoSource?> InspectSourceAsync(string path)
    {
        using var cts = new CancellationTokenSource(CollectorTimeout);
        if (!_fileSystem.DirectoryExists(path))
            return null;

        var gitDir = Path.Combine(path, ".git");
        if (!_fileSystem.DirectoryExists(gitDir) && !_fileSystem.Exists(gitDir))
            return new InfoSource(path, null, null, InfoSourceKind.NotGitRepo);

        try
        {
            var (headExit, headOut, _) = await WithTimeout(
                _commandExecutor.ExecuteAsync("git", ["-C", path, "rev-parse", "--short", "HEAD"], cancellationToken: cts.Token),
                cts.Token);
            if (headExit != 0)
                return new InfoSource(path, null, null, InfoSourceKind.NotGitRepo);

            var sha = headOut.Trim();
            if (string.IsNullOrWhiteSpace(sha))
                return new InfoSource(path, null, null, InfoSourceKind.NotGitRepo);

            var (subjExit, subjOut, _) = await WithTimeout(
                _commandExecutor.ExecuteAsync("git", ["-C", path, "log", "-1", "--format=%s"], cancellationToken: cts.Token),
                cts.Token);
            var subject = subjExit == 0 ? subjOut.Trim() : string.Empty;
            return new InfoSource(path, sha, string.IsNullOrEmpty(subject) ? null : subject, InfoSourceKind.Resolved);
        }
        catch
        {
            return new InfoSource(path, null, null, InfoSourceKind.NotGitRepo);
        }
    }

    internal async Task<string?> ComputeDiskUsageAsync(string path, CancellationToken ct)
    {
        try
        {
            var (exit, stdout, _) = await WithTimeout(
                _commandExecutor.ExecuteAsync("du", ["-sh", path]),
                ct);
            if (exit == 0 && !string.IsNullOrWhiteSpace(stdout))
            {
                var firstLine = stdout.Split('\n').FirstOrDefault()?.Trim();
                if (!string.IsNullOrWhiteSpace(firstLine))
                {
                    var parts = firstLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    return parts.Length > 0 ? parts[0] : firstLine;
                }
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    internal static string? TryGetBinaryPath(Assembly assembly)
    {
        try
        {
            var location = AppContext.BaseDirectory;
            if (string.IsNullOrWhiteSpace(location))
                return null;
            return location;
        }
        catch
        {
            return null;
        }
    }

    private Task<string?> ReadActiveProjectIdAsync() => _api.TryReadActiveProjectIdAsync();

    internal static async Task<T> WithTimeout<T>(Task<T> task, CancellationToken ct)
    {
        if (task.IsCompleted)
            return await task;
        var timeout = Task.Delay(Timeout.Infinite, ct);
        var completed = await Task.WhenAny(task, timeout);
        if (completed == task)
            return await task;
        throw new TimeoutException();
    }

    internal static async Task<T> SafeAsync<T>(Func<Task<T>> producer)
    {
        try
        {
            return await producer();
        }
        catch
        {
            return default!;
        }
    }
}
