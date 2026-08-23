using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mohist.Cli;

internal sealed partial class UpdateOperations
{
    private const string SlackRecoveryMutating = "mutating";
    private const string SlackRecoveryCommitted = "committed";
    private static readonly JsonSerializerOptions SlackSnapshotJsonOptions = new(JsonSerializerDefaults.Web);

    private string ResolveSlackRecoveryDirectory()
    {
        var home = _getUserHome?.Invoke();
        if (string.IsNullOrWhiteSpace(home))
            home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
            throw new InvalidOperationException("User home is required for Slack update recovery.");
        return Path.Combine(home, ".mohist", "update", "slack");
    }

    private static string CreateSlackRecoverySnapshotId(string installedBinary) =>
        Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(installedBinary.Replace('\\', '/'))))
            .ToLowerInvariant();

    private static string ResolveSlackRecoverySnapshotPath(string recoveryDirectory, string snapshotId) =>
        Path.Combine(recoveryDirectory, $"{snapshotId}.service-snapshot.json");

    private static bool IsValidSlackRecoverySnapshotId(string snapshotId) =>
        snapshotId.Length == 64
        && snapshotId.All(static value => value is >= '0' and <= '9' or >= 'a' and <= 'f');

    private SlackUpdateRecoveryManifest? PersistSlackRecoveryState(
        SlackServiceSnapshot snapshot,
        string snapshotPath,
        string recoveryMarker,
        bool hadPreviousBinary,
        string backupBinary,
        string binaryName,
        string snapshotId,
        bool wasNodeLauncher,
        string globalRecoveryMarker)
    {
        try
        {
            var snapshotDir = Path.GetDirectoryName(snapshotPath);
            if (!string.IsNullOrWhiteSpace(snapshotDir) && !_fileSystem.DirectoryExists(snapshotDir))
                _fileSystem.CreateDirectory(snapshotDir);
            _fileSystem.WriteAllTextUserOnly(
                snapshotPath,
                JsonSerializer.Serialize(snapshot, SlackSnapshotJsonOptions));
            if (!_fileSystem.IsUserOnlyFile(snapshotPath))
                throw new IOException($"Slack recovery snapshot permissions are too broad: {snapshotPath}");
            var manifest = new SlackUpdateRecoveryManifest(
                SlackRecoveryMutating,
                hadPreviousBinary,
                hadPreviousBinary ? HashSlackRecoveryFile(backupBinary) : null,
                binaryName,
                snapshotId,
                wasNodeLauncher);
            _fileSystem.WriteAllTextUserOnly(
                recoveryMarker,
                JsonSerializer.Serialize(manifest, SlackSnapshotJsonOptions));
            _fileSystem.WriteAllTextUserOnly(
                globalRecoveryMarker,
                JsonSerializer.Serialize(manifest, SlackSnapshotJsonOptions));
            if (!_fileSystem.IsUserOnlyFile(globalRecoveryMarker))
                throw new IOException($"Slack global recovery marker permissions are too broad: {globalRecoveryMarker}");
            return manifest;
        }
        catch (Exception ex)
        {
            _err.WriteLine($"Slack recovery state could not be persisted: {ex.Message}");
            return null;
        }
    }

    private bool MarkSlackRecoveryCommitted(
        string recoveryMarker,
        SlackUpdateRecoveryManifest manifest)
    {
        var committedMarker = $"{recoveryMarker}.committed.tmp";
        try
        {
            _fileSystem.WriteAllTextUserOnly(
                committedMarker,
                JsonSerializer.Serialize(
                    manifest with { Phase = SlackRecoveryCommitted },
                    SlackSnapshotJsonOptions));
            _fileSystem.MoveFile(committedMarker, recoveryMarker);
            return true;
        }
        catch (Exception ex)
        {
            _err.WriteLine($"Slack recovery state could not be marked committed: {ex.Message}");
            return false;
        }
    }

    private async Task<int> RecoverInterruptedSlackUpdateAsync(
        string stagingDir,
        string recoveryMarker,
        string recoveryDirectory,
        string repoRoot,
        string installedBinary,
        string stagedBinary,
        string backupBinary,
        string globalRecoveryMarker,
        CancellationToken cancellationToken)
    {
        SlackUpdateRecoveryManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<SlackUpdateRecoveryManifest>(
                _fileSystem.ReadAllText(recoveryMarker),
                SlackSnapshotJsonOptions);
        }
        catch (Exception ex)
        {
            _err.WriteLine($"Slack recovery marker could not be read: {ex.Message}");
            return 1;
        }

        if (manifest is null
            || string.IsNullOrWhiteSpace(manifest.BinaryName)
            || !string.Equals(manifest.BinaryName, Path.GetFileName(installedBinary), StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(manifest.SnapshotId)
            || !IsValidSlackRecoverySnapshotId(manifest.SnapshotId))
        {
            _err.WriteLine($"Slack recovery manifest is invalid at {recoveryMarker}.");
            return 1;
        }
        var snapshotPath = ResolveSlackRecoverySnapshotPath(recoveryDirectory, manifest.SnapshotId);

        if (manifest.Phase == SlackRecoveryCommitted)
        {
            try
            {
                CleanupSlackRecoveryFiles(snapshotPath, stagingDir, globalRecoveryMarker);
                _out.WriteLine("Finalized the previously committed Slack update.");
                return 0;
            }
            catch (Exception ex)
            {
                _err.WriteLine($"Committed Slack update cleanup failed: {ex.Message}");
                return 1;
            }
        }
        if (manifest.Phase != SlackRecoveryMutating)
        {
            _err.WriteLine($"Slack recovery manifest phase is invalid at {recoveryMarker}.");
            return 1;
        }

        SlackServiceSnapshot? snapshot;
        try
        {
            if (!_fileSystem.IsUserOnlyFile(snapshotPath))
            {
                _err.WriteLine($"Persisted Slack service snapshot permissions are too broad: {snapshotPath}.");
                return 1;
            }
            snapshot = JsonSerializer.Deserialize<SlackServiceSnapshot>(
                _fileSystem.ReadAllText(snapshotPath),
                SlackSnapshotJsonOptions);
        }
        catch (Exception ex)
        {
            _err.WriteLine($"Persisted Slack service snapshot could not be read: {ex.Message}");
            return 1;
        }
        if (snapshot is null
            || string.IsNullOrWhiteSpace(snapshot.Kind)
            || string.IsNullOrWhiteSpace(snapshot.LaunchPath)
            || snapshot.LaunchContent is null)
        {
            _err.WriteLine($"Persisted Slack service snapshot is invalid at {snapshotPath}.");
            return 1;
        }

        if (manifest.HadPreviousBinary)
        {
            if (!_fileSystem.Exists(backupBinary)
                || string.IsNullOrWhiteSpace(manifest.PreviousBinarySha256)
                || !string.Equals(
                    HashSlackRecoveryFile(backupBinary),
                    manifest.PreviousBinarySha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                _err.WriteLine("Slack recovery requires the previous binary, but its durable backup is missing or invalid.");
                return 1;
            }
        }
        else if (_fileSystem.Exists(backupBinary) || manifest.PreviousBinarySha256 is not null)
        {
            _err.WriteLine("Slack recovery manifest does not match the staged binary backup.");
            return 1;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var serviceOptions = new ServiceCommandOptions(false, _unitDir, 100, false);
        var requiresRollForward = manifest.WasNodeLauncher || !manifest.HadPreviousBinary;
        int stop;
        try
        {
            stop = await _systemd.StopSlackAsync(serviceOptions, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (requiresRollForward)
            {
                try
                {
                    stop = await _systemd.StopSlackAsync(serviceOptions, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _err.WriteLine($"Interrupted Slack first-migration recovery could not confirm service stop after cancellation: {ex.Message}");
                    throw;
                }
                if (stop != 0)
                {
                    _err.WriteLine("Interrupted Slack first-migration recovery could not confirm service stop after cancellation.");
                    throw;
                }
            }
            else
            {
                try
                {
                    var restart = await _systemd.StartSlackAsync(serviceOptions, CancellationToken.None);
                    if (restart != 0 || !await WaitForSlackRunningAsync(CancellationToken.None))
                        _err.WriteLine("Interrupted Slack update recovery could not restart the service after cancellation.");
                }
                catch (Exception ex)
                {
                    _err.WriteLine($"Interrupted Slack update recovery could not restart the service after cancellation: {ex.Message}");
                }
                throw;
            }
        }
        catch (Exception ex)
        {
            _err.WriteLine($"Interrupted Slack update recovery could not confirm service stop: {ex.Message}");
            return 1;
        }
        if (stop != 0)
        {
            _err.WriteLine("Interrupted Slack update recovery could not confirm service stop.");
            return stop;
        }

        try
        {
            var restore = await _systemd.RestoreSlackServiceAsync(snapshot, CancellationToken.None);
            if (restore != 0)
            {
                _err.WriteLine("Interrupted Slack update launcher recovery failed.");
                return restore;
            }

            if (!requiresRollForward)
            {
                var rollbackBinary = $"{installedBinary}.rollback.tmp";
                _fileSystem.CopyFileDurable(backupBinary, rollbackBinary);
                _fileSystem.MoveFile(rollbackBinary, installedBinary);
            }
            else
            {
                if (_fileSystem.Exists(stagedBinary))
                {
                    _fileSystem.MoveFile(stagedBinary, installedBinary);
                }
                else if (!_fileSystem.Exists(installedBinary))
                {
                    throw new FileNotFoundException(
                        "The staged Go binary is unavailable for first-migration recovery.",
                        stagedBinary);
                }
                var refresh = await _systemd.RefreshSlackServiceAsync(
                    repoRoot,
                    _unitDir,
                    CancellationToken.None);
                if (refresh != 0)
                {
                    _err.WriteLine("Interrupted Slack first-migration launcher recovery failed.");
                    return refresh;
                }
            }
        }
        catch (Exception ex)
        {
            _err.WriteLine($"Interrupted Slack update file recovery failed: {ex.Message}");
            return 1;
        }

        try
        {
            var start = await _systemd.StartSlackAsync(serviceOptions, CancellationToken.None);
            if (start != 0 || !await WaitForSlackRunningAsync(CancellationToken.None))
            {
                _err.WriteLine("Interrupted Slack update recovery could not restart the previous service.");
                return start != 0 ? start : 1;
            }
        }
        catch (Exception ex)
        {
            _err.WriteLine($"Interrupted Slack update recovery could not restart the previous service: {ex.Message}");
            return 1;
        }

        if (!MarkSlackRecoveryCommitted(recoveryMarker, manifest)) return 1;
        try
        {
            CleanupSlackRecoveryFiles(snapshotPath, stagingDir, globalRecoveryMarker);
        }
        catch (Exception ex)
        {
            _err.WriteLine($"Recovered Slack update cleanup failed: {ex.Message}");
            return 1;
        }
        _out.WriteLine(!requiresRollForward
            ? "Recovered the previous Slack service after an interrupted update. Run the update again to install the new build."
            : "Completed the interrupted first migration to the Go Slack service.");
        cancellationToken.ThrowIfCancellationRequested();
        return 0;
    }

    private string HashSlackRecoveryFile(string path)
    {
        using var stream = _fileSystem.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private void CleanupSlackRecoveryFiles(
        string snapshotPath,
        string stagingDir,
        string globalRecoveryMarker)
    {
        if (_fileSystem.Exists(globalRecoveryMarker))
            _fileSystem.Delete(globalRecoveryMarker);
        if (_fileSystem.Exists(snapshotPath))
            _fileSystem.Delete(snapshotPath);
        if (_fileSystem.DirectoryExists(stagingDir))
            _fileSystem.DeleteDirectory(stagingDir);
    }

    private async Task<bool> WaitForSlackRunningAsync(CancellationToken cancellationToken)
    {
        var deadline = _timeProvider.GetUtcNow() + SlackActivationTimeout;
        var consecutiveActiveProbes = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await _systemd.IsSlackRunningAsync(cancellationToken))
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
                remaining < SlackActivationPollInterval ? remaining : SlackActivationPollInterval,
                cancellationToken);
        }
    }

    private static bool IsNodeSlackServiceSnapshot(SlackServiceSnapshot snapshot) =>
        snapshot.LaunchContent.Replace('\\', '/').Contains(
            "packages/mohist-slack/dist/cli.js",
            StringComparison.OrdinalIgnoreCase);
}
