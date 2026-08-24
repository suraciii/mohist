namespace Mohist.Cli;

internal sealed partial class SystemdServiceInstaller
{
    public string SlackBinaryName => "mohist-slack";

    public async Task<int> InstallSlackAsync(ServiceInstallOptions options)
    {
        if (!EnsureSystemdSupported(options.DryRun)) return 1;
        var repoRoot = ResolveRepoRoot(options.RepoRoot);
        var environment = BuildServiceEnvironment(includeOperatorToken: true);
        var loadCredentials = Array.Empty<string>();
        if (!environment.ContainsKey(AdapterTokenEnvironmentVariable))
        {
            environment[AdapterTokenPathEnvironmentVariable] = $"%d/{OperatorCredentialName}";
            loadCredentials = [$"{OperatorCredentialName}:{ResolveOperatorCredentialSource()}"];
        }
        environment["SERVER_URL"] = options.ServerUrl ?? "http://127.0.0.1:3456";
        var unit = new SystemdUnit(
            Name: SlackUnit,
            Description: "Mohist Slack adapter",
            WorkingDirectory: repoRoot,
            ExecStart: QuoteSystemdArgument(Path.Combine(repoRoot, "packages/go/mohist-slack/bin/mohist-slack").Replace('\\', '/')),
            Environment: environment,
            LoadCredentials: loadCredentials);
        _ = unit.Render();
        var userHome = ResolveSlackUserHome();
        using var transactionLock = options.DryRun || userHome is null
            ? null
            : _fileSystem.TryAcquireFileLock(SlackBinaryLayout.TransactionLock(userHome));
        if (!options.DryRun && transactionLock is null)
        {
            _err.WriteLine("Another Slack install or update is already running for this user.");
            return 1;
        }
        if (!options.DryRun
            && (_fileSystem.Exists(SlackBinaryLayout.GlobalRecoveryMarker(userHome!))
                || _fileSystem.DirectoryExists(SlackBinaryLayout.UpdateStagingDirectory(repoRoot))))
        {
            _err.WriteLine("An unresolved Slack update transaction exists. Run 'mo update slack' from its original repository before installing.");
            return 1;
        }
        var promote = SlackBinaryLayout.PromoteBuildArtifact(repoRoot, "mohist-slack", options.DryRun, _fileSystem, _out, _err);
        if (promote != 0) return promote;
        return await InstallAsync(unit, options);
    }

    private string? ResolveSlackUserHome()
    {
        var home = _environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrWhiteSpace(home))
            home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(home) ? null : home;
    }

    public async Task<int> RefreshSlackServiceAsync(
        string repoRoot,
        string? unitDir = null,
        CancellationToken cancellationToken = default)
    {
        if (!EnsureSystemdSupported(dryRun: false)) return 1;
        var root = ResolveRepoRoot(repoRoot);
        var unitPath = Path.Combine(ResolveUnitDir(unitDir), SlackUnit).Replace('\\', '/');
        if (!_fileSystem.Exists(unitPath))
        {
            _err.WriteLine($"Slack unit file not found: {unitPath}");
            return 1;
        }

        string original;
        try
        {
            var executable = QuoteSystemdArgument(Path.Combine(root, "packages/go/mohist-slack/bin/mohist-slack").Replace('\\', '/'));
            original = _fileSystem.ReadAllText(unitPath);
            var updated = RewriteSlackServiceLaunch(
                original,
                SystemdUnit.FormatWorkingDirectory(root),
                executable);
            var tempPath = $"{unitPath}.update.tmp";
            await _fileSystem.WriteAllTextAsync(tempPath, updated);
            _fileSystem.MoveFile(tempPath, unitPath);
        }
        catch (Exception ex)
        {
            _err.WriteLine($"Slack service launcher refresh failed: {ex.Message}");
            return 1;
        }

        int reload;
        string reloadErr;
        try
        {
            (reload, _, reloadErr) = await _commandExecutor.ExecuteAsync(
                "systemctl",
                ["--user", "daemon-reload"],
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _err.WriteLine($"Slack service daemon reload failed: {ex.Message}");
            var restore = await RestoreSlackServiceAsync(
                new SlackServiceSnapshot("systemd", unitPath, original),
                CancellationToken.None);
            if (restore != 0) _err.WriteLine("Slack service launcher rollback failed after daemon reload error.");
            return restore != 0 ? restore : 1;
        }
        if (reload != 0)
        {
            if (!string.IsNullOrWhiteSpace(reloadErr)) _err.Write(reloadErr);
            var restore = await RestoreSlackServiceAsync(
                new SlackServiceSnapshot("systemd", unitPath, original),
                CancellationToken.None);
            if (restore != 0)
            {
                _err.WriteLine("Slack service launcher rollback failed after daemon reload error.");
                return restore;
            }
        }
        return reload;
    }

    public Task<SlackServiceSnapshot?> CaptureSlackServiceAsync(
        string? unitDir = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!EnsureSystemdSupported(dryRun: false))
            return Task.FromResult<SlackServiceSnapshot?>(null);
        var unitPath = Path.Combine(ResolveUnitDir(unitDir), SlackUnit).Replace('\\', '/');
        if (!_fileSystem.Exists(unitPath))
        {
            _err.WriteLine($"Slack unit file not found: {unitPath}");
            return Task.FromResult<SlackServiceSnapshot?>(null);
        }
        return Task.FromResult<SlackServiceSnapshot?>(
            new SlackServiceSnapshot("systemd", unitPath, _fileSystem.ReadAllText(unitPath)));
    }

    public async Task<int> RestoreSlackServiceAsync(
        SlackServiceSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        if (snapshot.Kind != "systemd")
        {
            _err.WriteLine("Slack service snapshot does not belong to systemd.");
            return 1;
        }
        try
        {
            var restorePath = $"{snapshot.LaunchPath}.restore.tmp";
            await _fileSystem.WriteAllTextAsync(restorePath, snapshot.LaunchContent);
            _fileSystem.MoveFile(restorePath, snapshot.LaunchPath);
            var (reload, _, restoreErr) = await _commandExecutor.ExecuteAsync(
                "systemctl",
                ["--user", "daemon-reload"],
                cancellationToken: cancellationToken);
            if (!string.IsNullOrWhiteSpace(restoreErr)) _err.Write(restoreErr);
            return reload;
        }
        catch (Exception ex)
        {
            _err.WriteLine($"Slack unit rollback failed: {ex.Message}");
            return 1;
        }
    }
}
