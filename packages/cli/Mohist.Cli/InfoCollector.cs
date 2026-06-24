using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal sealed class InfoCollector
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

    internal async Task<InfoVerbose> CollectVerboseAsync(
        InfoService server,
        InfoService runner,
        InfoProject? project,
        InfoDataDir dataDir,
        bool systemdAvailable)
    {
        var sourcePath = runner.Source?.Path ?? server.Source?.Path;
        var isGitRepo = (sourcePath is not null) && (server.Source?.CommitShort is not null || runner.Source?.CommitShort is not null);

        using var sharedCts = new CancellationTokenSource(CollectorTimeout);
        var unitEnvTask = SafeAsync(() => TryGetRunnerUnitEnvironmentAsync(systemdAvailable, sharedCts.Token));

        var skillsTask = SafeAsync(() => GetSkillsVerboseAsync());
        var gitRemoteTask = SafeAsync(() => GetGitRemoteVerboseAsync(sourcePath));
        var opencodeTask = SafeAsync(GetOpencodeRuntimeVerboseAsync);
        var osRuntimeTask = SafeAsync(GetOsRuntimeVerboseAsync);
        var diskTask = SafeAsync(() => GetDiskUsageVerboseAsync(dataDir));

        await Task.WhenAll(skillsTask, gitRemoteTask, opencodeTask, osRuntimeTask, diskTask, unitEnvTask);
        var unitEnv = await unitEnvTask;

        var envVarsTask = SafeAsync(() => GetEnvVarsVerboseAsync(runner, systemdAvailable, unitEnv));
        var capacityTask = SafeAsync(() => GetCapacityVerboseAsync(runner, project, systemdAvailable, unitEnv));
        await Task.WhenAll(envVarsTask, capacityTask);

        return new InfoVerbose(
            Skills: await skillsTask,
            GitRemote: await gitRemoteTask,
            OpencodeRuntime: await opencodeTask,
            EnvVars: await envVarsTask,
            OsRuntime: await osRuntimeTask,
            Capacity: await capacityTask,
            DiskUsage: await diskTask);
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

    internal async Task<InfoVerboseSkills> GetSkillsVerboseAsync()
    {
        try
        {
            if (_skillAssetService is null)
                return new InfoVerboseSkills(Array.Empty<InfoVerboseSkill>(), Resolved: true);

            var assets = _skillAssetService.ListVisibleSkills();
            var skills = new List<InfoVerboseSkill>(assets.Count);
            foreach (var asset in assets)
            {
                var installPath = TryGetSkillInstallPath(asset.Name);
                skills.Add(new InfoVerboseSkill(asset.Name, installPath));
            }
            return new InfoVerboseSkills(skills, Resolved: true);
        }
        catch
        {
            return new InfoVerboseSkills(Array.Empty<InfoVerboseSkill>(), Resolved: false);
        }
    }

    private string? TryGetSkillInstallPath(string skillName)
    {
        try
        {
            if (_skillAssetService is null)
                return null;
            var result = _skillAssetService.GetSkill(skillName, includeSupplementaryFiles: false);
            if (result.Found && result.Skill is not null)
                return result.Skill.DirectoryPath;
            return null;
        }
        catch
        {
            return null;
        }
    }

    internal async Task<InfoVerboseGitRemote> GetGitRemoteVerboseAsync(string? sourcePath)
    {
        using var cts = new CancellationTokenSource(CollectorTimeout);
        if (string.IsNullOrWhiteSpace(sourcePath))
            return new InfoVerboseGitRemote(null, IsGitRepo: false);
        if (!_fileSystem.DirectoryExists(sourcePath))
            return new InfoVerboseGitRemote(null, IsGitRepo: false);
        var gitDir = Path.Combine(sourcePath, ".git");
        if (!_fileSystem.DirectoryExists(gitDir) && !_fileSystem.Exists(gitDir))
            return new InfoVerboseGitRemote(null, IsGitRepo: false);

        try
        {
            var (exit, stdout, _) = await WithTimeout(
                _commandExecutor.ExecuteAsync("git", ["-C", sourcePath, "remote", "get-url", "origin"]),
                cts.Token);
            if (exit != 0)
                return new InfoVerboseGitRemote(null, IsGitRepo: true);
            var url = stdout.Trim();
            if (string.IsNullOrWhiteSpace(url))
                return new InfoVerboseGitRemote(null, IsGitRepo: true);
            return new InfoVerboseGitRemote(url, IsGitRepo: true);
        }
        catch
        {
            return new InfoVerboseGitRemote(null, IsGitRepo: true);
        }
    }

    internal async Task<InfoVerboseOpencodeRuntime> GetOpencodeRuntimeVerboseAsync()
    {
        using var cts = new CancellationTokenSource(CollectorTimeout);
        try
        {
            var rawCommand = _environment.GetEnvironmentVariable("MOHIST_AGENT_COMMAND") ?? "opencode";
            var (resolvedCommand, commandAllowed) = ValidateAgentCommand(rawCommand);
            string? version = null;
            if (commandAllowed)
            {
                try
                {
                    var (exit, stdout, _) = await WithTimeout(
                        _commandExecutor.ExecuteAsync(resolvedCommand!, ["--version"], cancellationToken: cts.Token),
                        cts.Token);
                    if (exit == 0 && !string.IsNullOrWhiteSpace(stdout))
                        version = stdout.Trim().Split('\n').FirstOrDefault()?.Trim();
                }
                catch
                {
                    version = null;
                }
            }

            int? modelCount = null;
            try
            {
                using var response = await _api.Http.GetAsync("/api/opencode/runtime", cts.Token);
                if (response.IsSuccessStatusCode)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
                    if (stream.Length > 0)
                    {
                        var node = await System.Text.Json.Nodes.JsonNode.ParseAsync(stream, cancellationToken: cts.Token);
                        var models = node?["data"]?["models"] as System.Text.Json.Nodes.JsonArray;
                        if (models is not null)
                            modelCount = models.Count;
                        else
                        {
                            var single = node?["data"]?["model"];
                            if (single is System.Text.Json.Nodes.JsonValue)
                                modelCount = 1;
                            else if (single is System.Text.Json.Nodes.JsonArray arr)
                                modelCount = arr.Count;
                        }
                    }
                }
            }
            catch
            {
                modelCount = null;
            }

            return new InfoVerboseOpencodeRuntime(commandAllowed ? resolvedCommand : null, version, modelCount, Resolved: commandAllowed);
        }
        catch
        {
            return new InfoVerboseOpencodeRuntime(null, null, null, Resolved: false);
        }
    }

    private static (string? Command, bool Allowed) ValidateAgentCommand(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return (null, false);
        var basename = Path.GetFileName(raw);
        if (string.IsNullOrEmpty(basename))
            return (null, false);
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "opencode", "opencode.exe" };
        if (!allowed.Contains(basename))
            return (null, false);
        return (basename, true);
    }

    internal async Task<IReadOnlyList<InfoVerboseEnvVar>> GetEnvVarsVerboseAsync(InfoService runner, bool systemdAvailable, IReadOnlyDictionary<string, string>? unitEnv = null)
    {
        using var cts = new CancellationTokenSource(CollectorTimeout);
        var collected = new Dictionary<string, string?>(StringComparer.Ordinal);

        IReadOnlyDictionary<string, string>? envSource = unitEnv;
        envSource ??= await TryGetRunnerUnitEnvironmentAsync(systemdAvailable, cts.Token);
        if (envSource is not null && runner.Status is { State: "active" })
        {
            foreach (var kvp in envSource)
            {
                if (IsWatchedEnvVar(kvp.Key))
                    collected[kvp.Key] = kvp.Value;
            }
        }

        foreach (var key in WatchedEnvVarNames)
        {
            if (!collected.ContainsKey(key))
            {
                var value = _environment.GetEnvironmentVariable(key);
                if (value is not null)
                    collected[key] = value;
            }
        }

        return collected
            .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
            .Select(kvp => new InfoVerboseEnvVar(kvp.Key, kvp.Value))
            .ToArray();
    }

    internal async Task<IReadOnlyDictionary<string, string>?> TryGetRunnerUnitEnvironmentAsync(bool systemdAvailable, CancellationToken ct)
    {
        if (!systemdAvailable)
            return null;
        try
        {
            var (exit, stdout, _) = await WithTimeout(
                _commandExecutor.ExecuteAsync("systemctl", [
                    "--user",
                    "show",
                    SystemdUnitParser.RunnerUnit,
                    "-p",
                    "Environment",
                ], cancellationToken: ct),
                ct);
            if (exit != 0)
                return null;
            return SystemdUnitParser.ParseSystemdEnvironment(stdout);
        }
        catch
        {
            return null;
        }
    }

    internal async Task<InfoVerboseOsRuntime> GetOsRuntimeVerboseAsync()
    {
        using var cts = new CancellationTokenSource(CollectorTimeout);
        string? os = null;
        string? arch = null;
        string? dotnet = null;
        string? node = null;

        try { os = GetOsName(); } catch { }
        try { arch = GetArchitecture(); } catch { }
        try { dotnet = RuntimeInformation.FrameworkDescription; } catch { }
        try
        {
            var (exit, stdout, _) = await WithTimeout(
                _commandExecutor.ExecuteAsync("node", ["--version"]),
                cts.Token);
            if (exit == 0 && !string.IsNullOrWhiteSpace(stdout))
                node = stdout.Trim().Split('\n').FirstOrDefault()?.Trim();
        }
        catch
        {
        }

        return new InfoVerboseOsRuntime(os, arch, dotnet, node);
    }

    private static string? GetOsName()
    {
        if (OperatingSystem.IsLinux()) return "linux";
        if (OperatingSystem.IsMacOS()) return "darwin";
        if (OperatingSystem.IsWindows()) return "windows";
        return "unknown";
    }

    private static string? GetArchitecture()
    {
        return RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
    }

    internal async Task<InfoVerboseCapacity> GetCapacityVerboseAsync(
        InfoService runner,
        InfoProject? project,
        bool systemdAvailable,
        IReadOnlyDictionary<string, string>? unitEnv = null)
    {
        using var cts = new CancellationTokenSource(CollectorTimeout);
        int? maxFromUnit = null;
        int? maxFromEnv = null;
        int? active = null;

        IReadOnlyDictionary<string, string>? envSource = unitEnv;
        envSource ??= await TryGetRunnerUnitEnvironmentAsync(systemdAvailable, cts.Token);
        if (runner.Status is { State: "active" } && envSource is not null)
        {
            if (envSource.TryGetValue("MAX_CONCURRENT_WORKFLOWS", out var maxText)
                && int.TryParse(maxText, out var parsed)
                && parsed > 0)
            {
                maxFromUnit = parsed;
            }
        }

        var maxFromEnvText = _environment.GetEnvironmentVariable("MAX_CONCURRENT_WORKFLOWS");
        if (maxFromEnvText is not null
            && int.TryParse(maxFromEnvText, out var maxEnvParsed)
            && maxEnvParsed > 0)
        {
            maxFromEnv = maxEnvParsed;
        }

        if (project is not null && !string.IsNullOrWhiteSpace(project.Id))
        {
            try
            {
                var path = $"/api/projects/{Uri.EscapeDataString(project.Id!)}/agent/status";
                using var response = await _api.Http.GetAsync(path, cts.Token);
                if (response.IsSuccessStatusCode)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
                    if (stream.Length > 0)
                    {
                        var node = await System.Text.Json.Nodes.JsonNode.ParseAsync(stream, cancellationToken: cts.Token);
                        var cap = node?["data"]?["capacity"];
                        if (cap is not null)
                        {
                            var a = cap["active"]?.GetValue<int?>();
                            var m = cap["max"]?.GetValue<int?>();
                            if (a is not null) active = a;
                            if (m is not null) maxFromUnit ??= m;
                        }
                    }
                }
            }
            catch
            {
            }
        }

        var max = maxFromUnit ?? maxFromEnv;
        return new InfoVerboseCapacity(active, max);
    }

    internal async Task<InfoVerboseDiskUsage> GetDiskUsageVerboseAsync(InfoDataDir dataDir)
    {
        using var cts = new CancellationTokenSource(CollectorTimeout);
        var dataRoot = dataDir.Path;
        if (string.IsNullOrWhiteSpace(dataRoot) || !_fileSystem.DirectoryExists(dataRoot))
            return new InfoVerboseDiskUsage(Array.Empty<InfoVerboseDiskCategory>(), Resolved: true);

        var projectsTask = ComputeCategorySizeAsync(Path.Combine(dataRoot, "projects"), cts.Token);
        var logsTask = ComputeCategorySizeAsync(Path.Combine(dataRoot, "logs"), cts.Token);
        var worktreesTask = ComputeCategorySizeAsync(Path.Combine(dataRoot, "worktrees"), cts.Token);
        await Task.WhenAll(projectsTask, logsTask, worktreesTask);
        var projectsSize = await projectsTask;
        var logsSize = await logsTask;
        var worktreesSize = await worktreesTask;

        var categories = new List<InfoVerboseDiskCategory>
        {
            new("projects", projectsSize.Size, projectsSize.FileCount),
            new("logs", logsSize.Size, logsSize.FileCount),
            new("worktrees", worktreesSize.Size, worktreesSize.FileCount),
        };
        return new InfoVerboseDiskUsage(categories, Resolved: true);
    }

    private async Task<(string? Size, int? FileCount)> ComputeCategorySizeAsync(string path, CancellationToken ct)
    {
        if (!_fileSystem.DirectoryExists(path))
            return (null, null);
        var size = await ComputeDiskUsageAsync(path, ct);
        int? fileCount = null;
        try
        {
            var files = _fileSystem.EnumerateFiles(path, "*", SearchOption.AllDirectories);
            fileCount = files.Count();
        }
        catch
        {
        }
        return (size, fileCount);
    }

    private static readonly string[] WatchedEnvVarNames =
    {
        "MOHIST_AGENT_COMMAND",
        "MOHIST_DB_PATH",
        "MOHIST_GIT_HASH",
        "MOHIST_RUNNER_ROOT",
        "MOHIST_SERVER_URL",
        "MOHIST_SKILLS_DIR",
        "MOHIST_WORKSPACE_ROOT",
        "MOHIST_ARTIFACT_ROOT",
        "MOHIST_CONFIG__AGENT_COMMAND",
        "MAX_CONCURRENT_WORKFLOWS",
        "RUNNER_ID",
        "SERVER_URL",
    };

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

    internal static string? ResolveSourcePath(SystemdUnitParser.SystemdUnitFields unit)
    {
        if (!string.IsNullOrWhiteSpace(unit.WorkingDirectory))
            return unit.WorkingDirectory;

        if (!string.IsNullOrWhiteSpace(unit.ExecStart))
        {
            var fromProject = ExtractProjectPath(unit.ExecStart!);
            if (!string.IsNullOrWhiteSpace(fromProject))
                return fromProject;

            var fromBinary = ExtractBinaryDirectory(unit.ExecStart!);
            if (!string.IsNullOrWhiteSpace(fromBinary))
                return fromBinary;
        }
        return null;
    }

    internal static string? ExtractProjectPath(string execStart)
    {
        var tokens = TokenizeExecStart(execStart);
        for (var i = 0; i < tokens.Count - 1; i++)
        {
            if (tokens[i] == "--project" && i + 1 < tokens.Count)
            {
                var path = StripQuotes(tokens[i + 1]);
                if (IsLikelyPath(path))
                {
                    if (path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                        || path.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase))
                    {
                        var dir = Path.GetDirectoryName(path);
                        if (!string.IsNullOrWhiteSpace(dir))
                            return dir;
                    }
                    return path;
                }
            }
        }
        return null;
    }

    internal static string? ExtractBinaryDirectory(string execStart)
    {
        var tokens = TokenizeExecStart(execStart);
        if (tokens.Count == 0)
            return null;
        var first = tokens[0];
        if (string.IsNullOrWhiteSpace(first) || first.StartsWith('-'))
            return null;
        if (IsRuntimeWrapper(first))
        {
            for (var i = 1; i < tokens.Count; i++)
            {
                if (tokens[i].StartsWith('-'))
                {
                    if (tokens[i] == "--" && i + 1 < tokens.Count)
                        return ExtractBinaryDirectoryFromCandidate(tokens[i + 1]);
                    continue;
                }
                if (IsRuntimeSubcommand(tokens[i]))
                    return null;
                return ExtractBinaryDirectoryFromCandidate(tokens[i]);
            }
            return null;
        }
        return ExtractBinaryDirectoryFromCandidate(first);
    }

    private static bool IsRuntimeWrapper(string token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        var basename = Path.GetFileName(token);
        return basename == "dotnet" || basename == "node" || token == "/usr/bin/env";
    }

    private static bool IsRuntimeSubcommand(string token)
    {
        return token is "run" or "exec" or "start" or "serve" or "dev";
    }

    private static string? ExtractBinaryDirectoryFromCandidate(string candidate)
    {
        var path = StripQuotes(candidate);
        if (string.IsNullOrWhiteSpace(path))
            return null;
        if (!IsAbsoluteOrProjectOrScript(path))
            return null;
        try
        {
            if (path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(dir))
                    return dir;
            }
            if (Directory.Exists(path))
                return path;
            var dir2 = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir2))
                return dir2;
        }
        catch
        {
        }
        return null;
    }

    private static bool IsAbsoluteOrProjectOrScript(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.StartsWith('/')) return true;
        if (path.StartsWith("./") || path.StartsWith("../")) return true;
        if (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':') return true;
        if (path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.EndsWith(".js", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static List<string> TokenizeExecStart(string execStart)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inSingle = false;
        var inDouble = false;
        foreach (var c in execStart)
        {
            if (c == '\'' && !inDouble) inSingle = !inSingle;
            else if (c == '"' && !inSingle) inDouble = !inDouble;
            else if (c == ' ' && !inSingle && !inDouble)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }
        if (current.Length > 0)
            tokens.Add(current.ToString());
        return tokens;
    }

    private static string StripQuotes(string value)
    {
        if (value.Length >= 2 && ((value[0] == '\'' && value[^1] == '\'') || (value[0] == '"' && value[^1] == '"')))
            return value[1..^1];
        return value;
    }

    private static bool IsLikelyPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (value.StartsWith('-')) return false;
        return value.StartsWith('/')
            || value.StartsWith("./")
            || value.StartsWith("../")
            || (value.Length >= 2 && char.IsLetter(value[0]) && value[1] == ':');
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
