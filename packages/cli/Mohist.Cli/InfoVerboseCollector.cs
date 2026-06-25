using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal sealed class InfoVerboseCollector
{
    private static readonly TimeSpan CollectorTimeout = TimeSpan.FromSeconds(2);

    private readonly IFileSystem _fileSystem;
    private readonly ICommandExecutor _commandExecutor;
    private readonly IEnvironmentVariableProvider _environment;
    private readonly MohistCliApi _api;
    private readonly SkillAssetService? _skillAssetService;

    public InfoVerboseCollector(
        IFileSystem fileSystem,
        ICommandExecutor commandExecutor,
        IEnvironmentVariableProvider environment,
        MohistCliApi api,
        SkillAssetService? skillAssetService = null)
    {
        _fileSystem = fileSystem;
        _commandExecutor = commandExecutor;
        _environment = environment;
        _api = api;
        _skillAssetService = skillAssetService;
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
        var unitEnvTask = InfoCollector.SafeAsync(() => TryGetRunnerUnitEnvironmentAsync(systemdAvailable, sharedCts.Token));

        var skillsTask = InfoCollector.SafeAsync(() => GetSkillsVerboseAsync());
        var gitRemoteTask = InfoCollector.SafeAsync(() => GetGitRemoteVerboseAsync(sourcePath));
        var opencodeTask = InfoCollector.SafeAsync(GetOpencodeRuntimeVerboseAsync);
        var osRuntimeTask = InfoCollector.SafeAsync(GetOsRuntimeVerboseAsync);
        var diskTask = InfoCollector.SafeAsync(() => GetDiskUsageVerboseAsync(dataDir));

        await Task.WhenAll(skillsTask, gitRemoteTask, opencodeTask, osRuntimeTask, diskTask, unitEnvTask);
        var unitEnv = await unitEnvTask;

        var envVarsTask = InfoCollector.SafeAsync(() => GetEnvVarsVerboseAsync(runner, systemdAvailable, unitEnv));
        var capacityTask = InfoCollector.SafeAsync(() => GetCapacityVerboseAsync(runner, project, systemdAvailable, unitEnv));
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
                if (WatchedEnvVarNames.Contains(kvp.Key, StringComparer.Ordinal))
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

    private async Task<string?> ComputeDiskUsageAsync(string path, CancellationToken ct)
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

    private static async Task<T> WithTimeout<T>(Task<T> task, CancellationToken ct)
    {
        return await InfoCollector.WithTimeout(task, ct);
    }
}
