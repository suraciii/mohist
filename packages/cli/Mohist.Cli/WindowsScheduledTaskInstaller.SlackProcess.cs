using System.Text;

namespace Mohist.Cli;

internal sealed partial class WindowsScheduledTaskInstaller
{
    private static readonly TimeSpan SlackInstallActivationTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SlackInstallActivationPollInterval = TimeSpan.FromSeconds(1);

    public string SlackBinaryName => "mohist-slack.exe";

    public async Task<int> InstallSlackAsync(ServiceInstallOptions options)
    {
        var repoRoot = ResolveRepoRoot(options.RepoRoot);
        var launcherPath = SlackLauncherPath();
        var taskName = SlackTaskName;
        var startupPath = SlackStartupPath();
        var metadataPath = SlackMetadataPath();
        var serverUrl = options.ServerUrl ?? "http://127.0.0.1:3456";
        var operatorToken = _environment.GetEnvironmentVariable(SlackAdapterTokenEnvironmentVariable);
        var spec = new SlackLauncherSpec(
            SanitizeForCmdAssignment(repoRoot),
            SanitizeForCmdAssignment(serverUrl),
            operatorToken is null ? null : SanitizeForCmdAssignment(operatorToken));
        var launcherBody = RenderSlackLauncher(spec);
        if (options.DryRun)
        {
            var dryRunPromote = SlackBinaryLayout.PromoteBuildArtifact(
                repoRoot, "mohist-slack.exe", true, _fileSystem, _out, _err);
            if (dryRunPromote != 0) return dryRunPromote;
            PreviewInstall(launcherPath, launcherBody, taskName);
            return 0;
        }

        var artifact = SlackBinaryLayout.BuildArtifact(repoRoot, "mohist-slack.exe");
        if (!_fileSystem.Exists(artifact))
        {
            _err.WriteLine($"Slack build artifact not found: {artifact}. Run 'npm run build:slack' first.");
            return 1;
        }

        using var transactionLock = _fileSystem.TryAcquireFileLock(
            SlackBinaryLayout.TransactionLock(_userProfilePath));
        if (transactionLock is null)
        {
            _err.WriteLine("Another Slack install or update is already running for this user.");
            return 1;
        }
        if (_fileSystem.Exists(SlackBinaryLayout.GlobalRecoveryMarker(_userProfilePath))
            || _fileSystem.DirectoryExists(SlackBinaryLayout.UpdateStagingDirectory(repoRoot)))
        {
            _err.WriteLine("An unresolved Slack update transaction exists. Run 'mo update slack' from its original repository before installing.");
            return 1;
        }

        var installedBinary = SlackBinaryLayout.InstalledBinary(repoRoot, "mohist-slack.exe");
        var binaryBackup = $"{installedBinary}.install.previous";
        if (_fileSystem.Exists(binaryBackup))
        {
            _err.WriteLine($"A previous Slack install backup still exists at {binaryBackup}; preserve and inspect it before retrying.");
            return 1;
        }

        bool launcherExisted;
        string? launcherBefore;
        bool startupExisted;
        string? startupBefore;
        bool metadataExisted;
        string? metadataBefore;
        try
        {
            launcherExisted = _fileSystem.Exists(launcherPath);
            launcherBefore = launcherExisted ? _fileSystem.ReadAllText(launcherPath) : null;
            startupExisted = _fileSystem.Exists(startupPath);
            startupBefore = startupExisted ? _fileSystem.ReadAllText(startupPath) : null;
            metadataExisted = _fileSystem.Exists(metadataPath);
            metadataBefore = metadataExisted ? _fileSystem.ReadAllText(metadataPath) : null;
        }
        catch (Exception ex)
        {
            _err.WriteLine($"Slack install state could not be captured: {ex.Message}");
            return 1;
        }

        var runningLegacyPids = new List<int>();
        if (IsLegacyNodeSlackLauncher(launcherPath))
        {
            var (queryCode, pids, queryError) = await QuerySlackProcessPidsAsync(
                launcherPath,
                startupPath,
                metadataPath);
            if (queryCode != 0)
            {
                if (!string.IsNullOrWhiteSpace(queryError)) _err.Write(queryError);
                _err.WriteLine("Running Node-era Slack state could not be verified; Slack install was not changed.");
                return queryCode;
            }
            runningLegacyPids.AddRange(pids);
        }

        var taskProbe = await ProbeSlackScheduledTaskAsync(taskName, launcherPath);
        if (taskProbe == ScheduledTaskProbe.Unknown)
        {
            _err.WriteLine("Scheduled Task state could not be verified; Slack install was not changed.");
            return 1;
        }
        if (taskProbe == ScheduledTaskProbe.Conflict)
        {
            _err.WriteLine($"Scheduled Task {taskName} is disabled, altered, or owned by another user; Slack install was not changed.");
            return 1;
        }

        var backend = taskProbe == ScheduledTaskProbe.Owned
            ? BackendKind.ScheduledTask
            : BackendKind.StartupFallback;
        var createdTask = false;
        var createArgs = BuildCreateTaskArgs(new TaskCreateSpec(taskName, QuoteForSchtasksTr(launcherPath)));
        if (taskProbe == ScheduledTaskProbe.Absent && !_fileSystem.Exists(startupPath))
        {
            var (createCode, _, createError) = await _commandExecutor.ExecuteAsync("schtasks", createArgs);
            if (createCode == 0)
            {
                backend = BackendKind.ScheduledTask;
                createdTask = true;
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(createError)) _err.Write(createError);
                var afterCreate = await ProbeSlackScheduledTaskAsync(taskName, launcherPath);
                if (afterCreate == ScheduledTaskProbe.Owned)
                {
                    backend = BackendKind.ScheduledTask;
                    createdTask = true;
                }
                else if (afterCreate == ScheduledTaskProbe.Conflict)
                {
                    _err.WriteLine($"Scheduled Task {taskName} appeared with an unexpected owner or definition; Slack install was not changed.");
                    return createCode;
                }
                else if (afterCreate == ScheduledTaskProbe.Unknown)
                {
                    _err.WriteLine("Scheduled Task creation failed and task absence could not be confirmed; Slack install was not changed.");
                    return createCode;
                }
            }
        }

        var hadInstalledBinary = _fileSystem.Exists(installedBinary);
        try
        {
            if (hadInstalledBinary)
                _fileSystem.CopyFileDurable(installedBinary, binaryBackup);
        }
        catch (Exception ex)
        {
            if (createdTask) await DeleteNewSlackScheduledTaskAsync(taskName);
            try
            {
                await RestoreInstallFileAsync(startupPath, startupExisted, startupBefore);
            }
            catch (Exception restoreEx)
            {
                _err.WriteLine($"Slack Startup-folder fallback rollback failed: {restoreEx.Message}");
            }
            _err.WriteLine($"Slack binary backup failed: {ex.Message}");
            return 1;
        }

        async Task RollbackFilesAsync()
        {
            try
            {
                if (hadInstalledBinary)
                {
                    var binaryRestore = $"{installedBinary}.install.restore";
                    _fileSystem.CopyFileDurable(binaryBackup, binaryRestore);
                    _fileSystem.MoveFile(binaryRestore, installedBinary);
                }
                else if (_fileSystem.Exists(installedBinary))
                {
                    _fileSystem.Delete(installedBinary);
                }
                await RestoreInstallFileAsync(launcherPath, launcherExisted, launcherBefore);
                await RestoreInstallFileAsync(startupPath, startupExisted, startupBefore);
                await RestoreInstallFileAsync(metadataPath, metadataExisted, metadataBefore);
                if (_fileSystem.Exists(binaryBackup)) _fileSystem.Delete(binaryBackup);
            }
            catch (Exception ex)
            {
                _err.WriteLine($"Slack install rollback failed: {ex.Message}");
            }
        }

        var promote = SlackBinaryLayout.PromoteBuildArtifact(
            repoRoot, "mohist-slack.exe", false, _fileSystem, _out, _err);
        if (promote != 0)
        {
            if (createdTask) await DeleteNewSlackScheduledTaskAsync(taskName);
            if (_fileSystem.Exists(binaryBackup)) _fileSystem.Delete(binaryBackup);
            return promote;
        }
        try
        {
            EnsureDirectory(launcherPath);
            await _fileSystem.WriteAllTextAsync(launcherPath, launcherBody);
            _out.WriteLine($"Wrote {launcherPath}");
            if (backend == BackendKind.StartupFallback)
            {
                await InstallStartupFallbackAsync(startupPath, launcherPath, metadataPath, repoRoot, serverUrl: serverUrl);
                _out.WriteLine("Installed with Startup-folder fallback.");
            }
            else
            {
                await WriteMetadataAsync(metadataPath, "scheduled-task", repoRoot, serverUrl: serverUrl);
                if (!RemoveSlackStartupFallback(startupPath))
                    throw new IOException("stale Slack Startup-folder fallback could not be removed");
                _out.WriteLine($"Registered Scheduled Task {taskName}");
            }

            if (runningLegacyPids.Count > 0)
            {
                var stopLegacy = await KillPidsAsync(runningLegacyPids, includeTree: true);
                if (stopLegacy != 0)
                    throw new IOException("running Node-era Slack process could not be stopped");
                int startGo;
                try
                {
                    startGo = await StartAsync(
                        taskName,
                        launcherPath,
                        startupPath,
                        metadataPath,
                        "Slack",
                        new ServiceCommandOptions(false, options.UnitDir, 100, false));
                }
                catch (Exception ex)
                {
                    _err.WriteLine($"Go Slack process could not be started after migration; the completed Go install was preserved for recovery: {ex.Message}");
                    return 1;
                }
                if (startGo != 0)
                {
                    _err.WriteLine("Go Slack process could not be started after migration; the completed Go install was preserved for recovery.");
                    return startGo;
                }
                bool stable;
                try
                {
                    stable = await WaitForTransferredSlackAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _err.WriteLine($"Go Slack activation could not be verified; the completed Go install was preserved for recovery: {ex.Message}");
                    return 1;
                }
                if (!stable)
                {
                    _err.WriteLine("Go Slack did not remain running after migration; the completed Go install was preserved for recovery.");
                    return 1;
                }
            }
        }
        catch (Exception ex)
        {
            await RollbackFilesAsync();
            if (createdTask) await DeleteNewSlackScheduledTaskAsync(taskName);
            if (runningLegacyPids.Count > 0)
            {
                var restoreStart = await StartAsync(
                    taskName,
                    launcherPath,
                    startupPath,
                    metadataPath,
                    "Slack",
                    new ServiceCommandOptions(false, options.UnitDir, 100, false));
                if (restoreStart != 0)
                    _err.WriteLine("Running Node-era Slack state could not be restored after install failure.");
            }
            _err.WriteLine($"Slack install failed: {ex.Message}");
            return 1;
        }
        if (_fileSystem.Exists(binaryBackup)) _fileSystem.Delete(binaryBackup);
        return 0;
    }

    public Task<SlackServiceSnapshot?> CaptureSlackServiceAsync(
        string? unitDir = null,
        CancellationToken cancellationToken = default)
    {
        _ = unitDir;
        cancellationToken.ThrowIfCancellationRequested();
        var launcherPath = SlackLauncherPath();
        if (!_fileSystem.Exists(launcherPath))
        {
            _err.WriteLine($"Slack launcher not found: {launcherPath}");
            return Task.FromResult<SlackServiceSnapshot?>(null);
        }
        var metadataPath = SlackMetadataPath();
        var metadataExisted = _fileSystem.Exists(metadataPath);
        return Task.FromResult<SlackServiceSnapshot?>(new SlackServiceSnapshot(
            "windows",
            launcherPath,
            _fileSystem.ReadAllText(launcherPath),
            metadataPath,
            metadataExisted ? _fileSystem.ReadAllText(metadataPath) : null,
            metadataExisted));
    }

    public async Task<int> RestoreSlackServiceAsync(
        SlackServiceSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (snapshot.Kind != "windows" || snapshot.MetadataPath is null)
        {
            _err.WriteLine("Slack service snapshot does not belong to Windows.");
            return 1;
        }
        try
        {
            var launcherRestore = $"{snapshot.LaunchPath}.restore.tmp";
            await _fileSystem.WriteAllTextAsync(launcherRestore, snapshot.LaunchContent);
            _fileSystem.MoveFile(launcherRestore, snapshot.LaunchPath);
            if (snapshot.MetadataExisted)
            {
                var metadataRestore = $"{snapshot.MetadataPath}.restore.tmp";
                await _fileSystem.WriteAllTextAsync(metadataRestore, snapshot.MetadataContent!);
                _fileSystem.MoveFile(metadataRestore, snapshot.MetadataPath);
            }
            else if (_fileSystem.Exists(snapshot.MetadataPath))
            {
                _fileSystem.Delete(snapshot.MetadataPath);
            }
            return 0;
        }
        catch (Exception ex)
        {
            _err.WriteLine($"Slack launcher rollback failed: {ex.Message}");
            return 1;
        }
    }

    public async Task<bool> IsSlackRunningAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (queryCode, pids, queryError) = await QuerySlackProcessPidsAsync(
            SlackLauncherPath(),
            SlackStartupPath(),
            SlackMetadataPath(),
            cancellationToken);
        if (queryCode != 0 && !string.IsNullOrWhiteSpace(queryError)) _err.Write(queryError);
        return queryCode == 0 && pids.Count > 0;
    }

    public async Task<int> RefreshSlackServiceAsync(
        string repoRoot,
        string? unitDir = null,
        CancellationToken cancellationToken = default)
    {
        _ = unitDir;
        cancellationToken.ThrowIfCancellationRequested();
        var root = CanonicalizeWindowsPath(repoRoot);
        var launcherPath = SlackLauncherPath();
        if (!_fileSystem.Exists(launcherPath))
        {
            _err.WriteLine($"Slack launcher not found: {launcherPath}");
            return 1;
        }

        var snapshot = await CaptureSlackServiceAsync(unitDir, cancellationToken);
        if (snapshot is null) return 1;
        var metadataPath = snapshot.MetadataPath!;
        var originalLauncher = snapshot.LaunchContent;
        try
        {
            var metadata = ReadMetadata(metadataPath);
            var backend = await DetectBackendAsync(
                SlackTaskName,
                SlackStartupPath(),
                launcherPath,
                metadataPath,
                cancellationToken);
            if (backend is BackendKind.None or BackendKind.Unknown)
                throw new InvalidOperationException("installed Slack service backend could not be verified");

            var updated = RewriteSlackLauncher(originalLauncher, root);
            var tempPath = $"{launcherPath}.update.tmp";
            await _fileSystem.WriteAllTextAsync(tempPath, updated);
            _fileSystem.MoveFile(tempPath, launcherPath);
            await WriteMetadataAsync(
                metadataPath,
                BackendLabel(backend),
                root,
                metadata?.ListenUrl,
                metadata?.ServerUrl ?? ReadSlackLauncherSetting(originalLauncher, "SERVER_URL"));
            if (backend == BackendKind.ScheduledTask && !RemoveSlackStartupFallback(SlackStartupPath()))
                throw new IOException("stale Slack Startup-folder fallback could not be removed");
        }
        catch (Exception ex)
        {
            _err.WriteLine($"Slack launcher refresh failed: {ex.Message}");
            var restore = await RestoreSlackServiceAsync(snapshot, CancellationToken.None);
            if (restore != 0) _err.WriteLine("Slack launcher rollback failed after refresh error.");
            return restore != 0 ? restore : 1;
        }

        _out.WriteLine($"Refreshed {launcherPath}");
        return 0;
    }

    private static string? ReadSlackLauncherSetting(string content, string name)
    {
        var prefix = $"set \"{name}=";
        var line = content
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(value => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (line is null || !line.EndsWith('"')) return null;
        return line[prefix.Length..^1];
    }

    private bool RemoveSlackStartupFallback(string startupPath)
    {
        if (!_fileSystem.Exists(startupPath)) return true;
        try
        {
            _fileSystem.Delete(startupPath);
            _out.WriteLine($"Removed stale Startup-folder fallback {startupPath}");
            return true;
        }
        catch (Exception ex)
        {
            _err.WriteLine($"Failed to remove stale Startup-folder fallback {startupPath}: {ex.Message}");
            return false;
        }
    }

    private async Task DeleteNewSlackScheduledTaskAsync(string taskName)
    {
        var (code, _, stderr) = await _commandExecutor.ExecuteAsync("schtasks", BuildDeleteArgs(taskName));
        if (code != 0)
            _err.WriteLine($"Failed to roll back the newly registered Slack task: {stderr}");
    }

    private async Task RestoreInstallFileAsync(string path, bool existed, string? content)
    {
        if (existed)
        {
            await _fileSystem.WriteAllTextAsync(path, content!);
        }
        else if (_fileSystem.Exists(path))
        {
            _fileSystem.Delete(path);
        }
    }

    private async Task<ScheduledTaskProbe> ProbeSlackScheduledTaskAsync(
        string taskName,
        string launcherPath,
        CancellationToken cancellationToken = default)
    {
        var safeTaskName = taskName.Replace("'", "''", StringComparison.Ordinal);
        var safeLauncherPath = launcherPath.Replace("'", "''", StringComparison.Ordinal);
        var script = "$ErrorActionPreference = 'Stop'; " +
                      "$service = New-Object -ComObject 'Schedule.Service'; " +
                      "$service.Connect(); $folder = $service.GetFolder('\\'); " +
                      $"$tasks = @($folder.GetTasks(1) | Where-Object {{ $_.Name -ieq '{safeTaskName}' }}); " +
                      "if ($tasks.Count -eq 0) { exit 2 }; if ($tasks.Count -ne 1) { exit 3 }; " +
                      "$task = $tasks[0]; $definition = $task.Definition; " +
                      "$actions = @($definition.Actions); $triggers = @($definition.Triggers); " +
                      "if (-not $task.Enabled -or $actions.Count -ne 1 -or $triggers.Count -ne 1) { exit 3 }; " +
                      "$current = [Security.Principal.WindowsIdentity]::GetCurrent(); " +
                      "$userId = [string]$definition.Principal.UserId; " +
                      "$owned = $userId -ieq $current.Name -or ($null -ne $current.User -and $userId -ieq $current.User.Value); " +
                      "if (-not $owned -or $actions[0].Type -ne 0 -or $triggers[0].Type -ne 9 -or -not $triggers[0].Enabled) { exit 3 }; " +
                      "$actual = [Environment]::ExpandEnvironmentVariables([string]$actions[0].Path).Trim('\"'); " +
                      $"$expected = '{safeLauncherPath}'; " +
                      "if ([IO.Path]::GetFullPath($actual) -ine [IO.Path]::GetFullPath($expected)) { exit 3 }; exit 0";
        var (code, _, _) = await _commandExecutor.ExecuteAsync(
            "powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command", script],
            cancellationToken: cancellationToken);
        return code switch
        {
            0 => ScheduledTaskProbe.Owned,
            2 => ScheduledTaskProbe.Absent,
            3 => ScheduledTaskProbe.Conflict,
            _ => ScheduledTaskProbe.Unknown,
        };
    }

    private async Task<bool> WaitForTransferredSlackAsync(CancellationToken cancellationToken)
    {
        var deadline = _timeProvider.GetUtcNow() + SlackInstallActivationTimeout;
        var consecutiveActiveProbes = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await IsSlackRunningAsync(cancellationToken))
            {
                consecutiveActiveProbes++;
                if (consecutiveActiveProbes == 2) return true;
            }
            else
            {
                consecutiveActiveProbes = 0;
            }

            var remaining = deadline - _timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero) return false;
            await _pollWait(
                remaining < SlackInstallActivationPollInterval ? remaining : SlackInstallActivationPollInterval,
                cancellationToken);
        }
    }

    private static string RewriteSlackLauncher(string content, string repoRoot)
    {
        var safeRoot = SanitizeForCmdAssignment(repoRoot);
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var rootCount = 0;
        var launchCount = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith("cd /d ", StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = $"cd /d {QuoteForCmdBody(safeRoot)}";
                rootCount++;
                continue;
            }
            if (trimmed.Contains(@"packages\mohist-slack\dist\cli.js", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains(@"packages\go\mohist-slack\bin\mohist-slack.exe", StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = "\"packages\\go\\mohist-slack\\bin\\mohist-slack.exe\" >> \"%USERPROFILE%\\.mohist\\slack\\out.log\" 2>&1";
                launchCount++;
            }
        }
        if (rootCount != 1 || launchCount != 1)
            throw new InvalidOperationException("Slack launcher must contain exactly one repository root and launch command");
        return string.Join(Environment.NewLine, lines);
    }

    private async Task<(int ExitCode, List<int> Pids, string Stderr)> QuerySlackProcessPidsAsync(
        string launcherPath,
        string startupPath,
        string metadataPath,
        CancellationToken cancellationToken = default)
    {
        if (IsLegacyNodeSlackLauncher(launcherPath))
        {
            return await QueryLauncherNodePidsAsync(launcherPath, startupPath, cancellationToken);
        }

        var executablePath = ResolveSlackExecutablePath(metadataPath, launcherPath);
        if (executablePath is null)
        {
            return (
                1,
                [],
                "Cannot safely stop Slack because its installed repository path is unavailable. Reinstall the Slack service.\n");
        }
        return await QueryExactProcessPidsAsync("mohist-slack.exe", executablePath, cancellationToken);
    }

    private bool IsLegacyNodeSlackLauncher(string launcherPath) =>
        _fileSystem.Exists(launcherPath)
        && _fileSystem.ReadAllText(launcherPath).Contains(
            @"packages\mohist-slack\dist\cli.js",
            StringComparison.OrdinalIgnoreCase);

    private string? ResolveSlackExecutablePath(string metadataPath, string launcherPath)
    {
        var repoRoot = ReadMetadata(metadataPath)?.RepoRoot;
        if (string.IsNullOrWhiteSpace(repoRoot) && _fileSystem.Exists(launcherPath))
            repoRoot = ReadRepoRootFromSlackLauncher(_fileSystem.ReadAllText(launcherPath));
        if (string.IsNullOrWhiteSpace(repoRoot)) return null;
        try
        {
            return CanonicalizeWindowsPath(Path.Combine(
                repoRoot,
                "packages",
                "go",
                "mohist-slack",
                "bin",
                "mohist-slack.exe"));
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadRepoRootFromSlackLauncher(string content)
    {
        const string prefix = "cd /d ";
        var line = content
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(value => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (line is null) return null;
        var encoded = line[prefix.Length..].Trim();
        if (encoded.Length >= 2 && encoded[0] == '"' && encoded[^1] == '"')
            encoded = encoded[1..^1];
        var decoded = new StringBuilder(encoded.Length);
        for (var i = 0; i < encoded.Length; i++)
        {
            if (encoded[i] == '^' && i + 1 < encoded.Length)
                i++;
            decoded.Append(encoded[i]);
        }
        return decoded.Length == 0 ? null : decoded.ToString();
    }

    private async Task<(int ExitCode, List<int> Pids, string Stderr)> QueryExactProcessPidsAsync(
        string imageName,
        string executablePath,
        CancellationToken cancellationToken = default)
    {
        var safeImageName = imageName.Replace("'", "''", StringComparison.Ordinal);
        var safeExecutablePath = executablePath.Replace("'", "''", StringComparison.Ordinal);
        var script = "$ErrorActionPreference = 'Stop'; " +
                     $"$path = '{safeExecutablePath}'; " +
                     $"Get-CimInstance Win32_Process -Filter \"Name = '{safeImageName}'\" -ErrorAction Stop | " +
                      "Where-Object { $_.ExecutablePath -ieq $path } | ForEach-Object { $_.ProcessId }";
        return await QueryPowerShellPidsAsync(script, cancellationToken);
    }

    private async Task<(int ExitCode, List<int> Pids, string Stderr)> QueryLauncherNodePidsAsync(
        string launcherPath,
        string startupPath,
        CancellationToken cancellationToken = default)
    {
        var safeLauncher = launcherPath.Replace("'", "''", StringComparison.Ordinal);
        var safeStartup = startupPath.Replace("'", "''", StringComparison.Ordinal);
        var script = "$ErrorActionPreference = 'Stop'; " +
                     $"$paths = @('{safeLauncher}', '{safeStartup}'); " +
                     "Get-CimInstance Win32_Process -Filter \"Name = 'cmd.exe'\" -ErrorAction Stop | " +
                     "Where-Object { $commandLine = $_.CommandLine; " +
                     "$commandLine -and @($paths | Where-Object { " +
                     "$commandLine.IndexOf($_, [System.StringComparison]::OrdinalIgnoreCase) -ge 0 }).Count -gt 0 } | " +
                     "ForEach-Object { $_.ProcessId }";
        return await QueryPowerShellPidsAsync(script, cancellationToken);
    }

    private async Task<(int ExitCode, List<int> Pids, string Stderr)> QueryPowerShellPidsAsync(
        string script,
        CancellationToken cancellationToken = default)
    {
        var (code, stdout, stderr) = await _commandExecutor.ExecuteAsync(
            "powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command", script],
            cancellationToken: cancellationToken);
        var pids = stdout
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => int.TryParse(value, out var pid) ? pid : 0)
            .Where(pid => pid > 0)
            .ToList();
        return (code, pids, stderr);
    }
}
