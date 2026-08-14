namespace Mohist.Cli;

internal sealed record ManagedCliLauncherState(
    string LauncherPath,
    string? BackupPath,
    bool HadPrevious,
    bool BackupCreated,
    bool Changed);

internal sealed class ManagedCliLauncher
{
    private readonly TextWriter _out;
    private readonly TextWriter _err;
    private readonly ICommandExecutor _commands;
    private readonly IFileSystem _files;

    public ManagedCliLauncher(
        TextWriter output,
        TextWriter error,
        ICommandExecutor commands,
        IFileSystem files)
    {
        _out = output;
        _err = error;
        _commands = commands;
        _files = files;
    }

    public async Task<int> InstallAsync(
        string launcherPath,
        string targetPath,
        CancellationToken cancellationToken = default)
    {
        var error = await WriteLauncherAsync(launcherPath, targetPath, cancellationToken);
        if (error is not null)
        {
            _err.WriteLine(error);
            return 1;
        }

        _out.WriteLine($"Installed CLI wrapper: {launcherPath}");
        return 0;
    }

    public async Task<(ManagedCliLauncherState? State, string? Error)> ActivateAsync(
        string launcherPath,
        string candidatePath,
        RuntimeIdentity candidate,
        string backupPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(launcherPath))
            return (null, "stable CLI launcher path is unavailable");
        if (!candidate.IsComplete)
            return (null, "candidate CLI identity is incomplete");
        if (_files.Exists(backupPath))
            return (null, $"CLI launcher backup already exists at '{backupPath}'");

        var hadPrevious = _files.Exists(launcherPath);
        var state = new ManagedCliLauncherState(
            launcherPath,
            hadPrevious ? backupPath : null,
            hadPrevious,
            BackupCreated: false,
            Changed: false);

        if (PointsToIdentity(launcherPath, candidate))
            return (state, null);

        try
        {
            var backupDirectory = Path.GetDirectoryName(backupPath);
            if (!string.IsNullOrWhiteSpace(backupDirectory))
                _files.CreateDirectory(backupDirectory);

            if (hadPrevious)
                _files.CopyFile(launcherPath, backupPath);

            var writeError = await WriteLauncherAsync(launcherPath, candidatePath, cancellationToken);
            if (writeError is not null)
                return (state with { BackupCreated = hadPrevious }, writeError);

            return (state with { BackupCreated = hadPrevious, Changed = true }, null);
        }
        catch (Exception ex)
        {
            CleanupTempFile($"{launcherPath}.tmp");
            return (
                state with { BackupCreated = hadPrevious && _files.Exists(backupPath) },
                $"CLI launcher activation failed: {ex.Message}");
        }
    }

    public Task<int> RestoreAsync(ManagedCliLauncherState? state)
    {
        if (state is null)
            return Task.FromResult(0);

        try
        {
            if (state.Changed)
            {
                if (state.HadPrevious)
                {
                    if (string.IsNullOrWhiteSpace(state.BackupPath) || !_files.Exists(state.BackupPath))
                        throw new InvalidOperationException("previous CLI launcher backup is missing");
                    _files.MoveFile(state.BackupPath, state.LauncherPath);
                }
                else if (_files.Exists(state.LauncherPath))
                {
                    _files.Delete(state.LauncherPath);
                }
            }

            if (state.BackupCreated
                && !state.Changed
                && !string.IsNullOrWhiteSpace(state.BackupPath)
                && _files.Exists(state.BackupPath))
            {
                _files.Delete(state.BackupPath);
            }

            return Task.FromResult(0);
        }
        catch (Exception ex)
        {
            _err.WriteLine($"CLI launcher restoration failed: {ex.Message}");
            return Task.FromResult(1);
        }
    }

    public Task<int> FinalizeAsync(ManagedCliLauncherState? state)
    {
        if (state is null
            || !state.BackupCreated
            || string.IsNullOrWhiteSpace(state.BackupPath))
        {
            return Task.FromResult(0);
        }

        try
        {
            if (_files.Exists(state.BackupPath))
                _files.Delete(state.BackupPath);
            return Task.FromResult(0);
        }
        catch (Exception ex)
        {
            _err.WriteLine($"CLI launcher backup cleanup failed: {ex.Message}");
            return Task.FromResult(1);
        }
    }

    private async Task<string?> WriteLauncherAsync(
        string launcherPath,
        string targetPath,
        CancellationToken cancellationToken)
    {
        var launcherDirectory = Path.GetDirectoryName(launcherPath);
        if (!string.IsNullOrWhiteSpace(launcherDirectory))
            _files.CreateDirectory(launcherDirectory);

        var tempPath = $"{launcherPath}.tmp";
        var launcher = "#!/bin/sh" + Environment.NewLine
            + $"exec \"{targetPath}\" \"$@\"" + Environment.NewLine;

        try
        {
            await _files.WriteAllTextAsync(tempPath, launcher);
            var (chmod, _, chmodError) = await _commands.ExecuteAsync(
                "chmod",
                ["+x", tempPath],
                null,
                cancellationToken);
            if (chmod != 0)
            {
                CleanupTempFile(tempPath);
                return string.IsNullOrWhiteSpace(chmodError)
                    ? $"Could not make wrapper script at {tempPath} executable."
                    : chmodError.Trim();
            }

            _files.MoveFile(tempPath, launcherPath);
            return null;
        }
        catch (Exception ex)
        {
            CleanupTempFile(tempPath);
            return $"Could not install wrapper script at {launcherPath}: {ex.Message}";
        }
    }

    private bool PointsToIdentity(string launcherPath, RuntimeIdentity candidate)
    {
        if (!_files.Exists(launcherPath))
            return false;

        try
        {
            var content = _files.ReadAllText(launcherPath);
            const string marker = "exec \"";
            const string suffix = "\" \"$@\"";
            var start = content.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0)
                return false;
            start += marker.Length;
            var end = content.IndexOf(suffix, start, StringComparison.Ordinal);
            if (end <= start)
                return false;

            var targetPath = content[start..end];
            var targetDirectory = Path.GetDirectoryName(targetPath);
            if (string.IsNullOrWhiteSpace(targetDirectory))
                return false;
            var identityPath = Path.Combine(targetDirectory, "runtime-identity.json").Replace('\\', '/');
            if (!_files.Exists(identityPath))
                return false;

            return RuntimeIdentity.Read(_files.ReadAllText(identityPath))?.Matches(candidate) == true;
        }
        catch
        {
            return false;
        }
    }

    private void CleanupTempFile(string path)
    {
        try
        {
            if (_files.Exists(path))
                _files.Delete(path);
        }
        catch
        {
        }
    }
}
