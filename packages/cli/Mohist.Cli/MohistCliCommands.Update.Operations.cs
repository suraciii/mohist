namespace Mohist.Cli;

internal sealed partial class SourceCodeUpdater
{
    public async Task<int> UpdateCliAsync(string? repoRoot, bool dryRun, string? cliPath = null, CancellationToken cancellationToken = default)
    {
        var root = ResolveRepoRoot(repoRoot);
        var home = _getUserHome?.Invoke();
        var primaryTarget = ResolveManagedCliPath(home);
        var alternateTarget = ResolveAlternateManagedCliPath(home);
        var currentProcessPath = Environment.ProcessPath?.Replace('\\', '/');
        var managedTarget = string.Equals(currentProcessPath, primaryTarget, StringComparison.OrdinalIgnoreCase)
            ? alternateTarget
            : primaryTarget;
        var target = !string.IsNullOrWhiteSpace(cliPath) ? await ResolveCliPathAsync(cliPath) : managedTarget;
        if (string.IsNullOrWhiteSpace(target))
        {
            _err.WriteLine("Could not resolve mo executable path. Pass --cli-path to update the CLI explicitly.");
            return 1;
        }

        var publishDir = Path.Combine(root, ".publish", "cli");
        var binary = Path.Combine(publishDir, "Mohist.Cli");
        var tempTarget = $"{target}.tmp";
        var sourceSkillData = Path.Combine(publishDir, "skill-data");
        var managedSkillData = ResolveManagedSkillAssetRoot();

        _out.WriteLine($"Updating CLI from source: {root}");

        if (dryRun)
        {
            _out.WriteLine("Dry run: would execute:");
            _out.WriteLine($"  cd {root} && dotnet publish packages/cli/Mohist.Cli/Mohist.Cli.csproj -c Release -r {RuntimeIdentifier()} --self-contained true /p:PublishSingleFile=true -o {publishDir}");
            _out.WriteLine($"  cp {binary} {tempTarget}");
            _out.WriteLine($"  chmod +x {tempTarget}");
            _out.WriteLine($"  mv {tempTarget} {target}");
            _out.WriteLine($"  synchronize {sourceSkillData} into {managedSkillData} (prepare temp dir, replace managed root)");
            if (target == primaryTarget || target == alternateTarget)
            {
                var wrapper = ResolveCliWrapperPath(home);
                _out.WriteLine($"  ensure wrapper script at {wrapper} -> {target}");
            }
            return 0;
        }

        var publishArgs = new[]
        {
            "publish",
            "packages/cli/Mohist.Cli/Mohist.Cli.csproj",
            "-c",
            "Release",
            "-r",
            RuntimeIdentifier(),
            "--self-contained",
            "true",
            "/p:PublishSingleFile=true",
            "-o",
            publishDir,
        };
        var (publish, publishOut, publishErr) = await _commandExecutor.ExecuteAsync("dotnet", publishArgs, root);
        if (publish != 0)
        {
            WriteCommandFailureOutput(publishOut, publishErr);
            _err.WriteLine("CLI publish failed. Aborting update.");
            return publish;
        }

        _fileSystem.CreateDirectory(Path.GetDirectoryName(target)!);

        var (copy, _, copyErr) = await _commandExecutor.ExecuteAsync("cp", [binary, tempTarget], root);
        if (copy != 0)
        {
            if (!string.IsNullOrWhiteSpace(copyErr)) _err.WriteLine(copyErr);
            _err.WriteLine("CLI install failed. Aborting update.");
            return copy;
        }

        var (chmod, _, chmodErr) = await _commandExecutor.ExecuteAsync("chmod", ["+x", tempTarget], root);
        if (chmod != 0)
        {
            if (!string.IsNullOrWhiteSpace(chmodErr)) _err.WriteLine(chmodErr);
            _err.WriteLine("CLI chmod failed. Aborting update.");
            return chmod;
        }

        var (move, _, moveErr) = await _commandExecutor.ExecuteAsync("mv", [tempTarget, target], root);
        if (move != 0)
        {
            if (!string.IsNullOrWhiteSpace(moveErr)) _err.WriteLine(moveErr);
            _err.WriteLine("CLI replace failed. Aborting update.");
            return move;
        }

        if (target == primaryTarget || target == alternateTarget)
        {
            var wrapperExit = await EnsureCliWrapperAsync(target, home);
            if (wrapperExit != 0)
            {
                _err.WriteLine("CLI wrapper installation failed. Aborting update.");
                return wrapperExit;
            }
        }

        var synchronizer = new SkillAssetSynchronizer(_out, _err, _fileSystem);
        var syncExitCode = await synchronizer.SyncAsync(sourceSkillData, managedSkillData);
        if (syncExitCode != 0)
        {
            _err.WriteLine("Managed skill asset sync failed. Aborting update.");
            return syncExitCode;
        }

        _out.WriteLine($"CLI updated: {target}");
        return 0;
    }

    public async Task<int> UpdateServerAsync(string? repoRoot, bool dryRun, CancellationToken cancellationToken = default)
    {
        var root = ResolveRepoRoot(repoRoot);

        _out.WriteLine($"Updating server from source: {root}");

        if (dryRun)
        {
            _out.WriteLine("Dry run: would execute:");
            _out.WriteLine($"  cd {root} && dotnet build Mohist.sln");
            _out.WriteLine($"  {RestartCommandLine("server")} (if installed)");
            _out.WriteLine("  wait for /api/health, /, and referenced /assets/* response headers readiness checks");
            await WriteServerScopeMessageAsync();
            return 0;
        }

        var exitCode = await BuildAndRestartServerAsync(root, cancellationToken);
        if (exitCode != 0)
        {
            return exitCode;
        }

        _out.WriteLine("Server service restarted.");
        var ready = await _readinessProbe.WaitForServerReadyAsync(_serverReadyTimeout, cancellationToken);
        if (!ready.Ready)
        {
            _err.WriteLine($"Server service restarted, but Mohist readiness checks did not pass within {(int)_serverReadyTimeout.TotalSeconds} seconds.");
            if (!string.IsNullOrWhiteSpace(ready.LastFailure))
                _err.WriteLine($"Last readiness error: {ready.LastFailure}");
            return 1;
        }

        _out.WriteLine("Server is ready.");
        await WriteServerScopeMessageAsync();
        return 0;
    }

    public async Task<int> UpdateRunnerAsync(string? repoRoot, bool dryRun, CancellationToken cancellationToken = default)
    {
        var root = ResolveRepoRoot(repoRoot);

        _out.WriteLine($"Updating runner from source: {root}");

        if (dryRun)
        {
            _out.WriteLine("Dry run: would execute:");
            _out.WriteLine($"  cd {root} && npm run build -w packages/runner");
            _out.WriteLine($"  {RestartCommandLine("runner")} (if installed)");
            _out.WriteLine("  wait for runner to reconnect, then read its buildGitHash from /api/runner/identity");
            return 0;
        }

        var installed = await _systemd.IsRunnerInstalledAsync(_unitDir);
        if (!installed)
        {
            var reason = "runner service is not installed";
            _out.WriteLine($"Runner refresh skipped: {reason}");
            _runnerRefreshVerifier.WriteSkippedSummary(reason, _out, _err);
            return 0;
        }

        var (build, buildOut, buildErr) = await _commandExecutor.ExecuteAsync("npm", ["run", "build", "-w", "packages/runner"], root);
        if (build != 0)
        {
            WriteCommandFailureOutput(buildOut, buildErr);
            _err.WriteLine("Build failed. Aborting update.");
            return build;
        }

        _out.WriteLine("Runner updated successfully.");

        var restart = await _systemd.RestartRunnerAsync(new ServiceCommandOptions(false, null, 100, false));
        if (restart != 0)
        {
            _err.WriteLine("Warning: Failed to restart runner service. You may need to restart manually.");
            return restart;
        }

        _out.WriteLine("Runner service restarted.");

        var outcome = await _runnerRefreshVerifier.VerifyRunnerRuntimeAsync(root);
        outcome.WriteSummary(_out, _err);
        return outcome.ExitCode;
    }
}
