using System.Globalization;
using Mohist.Server.Runner.Domain;

namespace Mohist.Server.Runner.Grains;

public partial class RunnerGrain
{
    public async Task UpdateBuildGitHashAsync(string? buildGitHash)
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            var normalized = RunnerBuildIdentityPolicy.Normalize(buildGitHash);
            if (_info is null)
            {
                _pendingBuildGitHash = normalized;
                return;
            }

            if (string.Equals(_info.BuildGitHash, normalized, StringComparison.Ordinal))
                return;

            SetRunnerInfo(_info with { BuildGitHash = normalized });
            await PersistAsync();
            _log.LogInformation("Runner {Id} reported buildGitHash {Hash}", RunnerId, normalized ?? "<null>");
            if (_status == RunnerStatus.Online)
                await UpsertRegistryAsync();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task UpdateRuntimeIdentityAsync(
        string? buildGitHash,
        string? component,
        string? version,
        string? sourceRevision,
        string? treeHash,
        string? artifactDigest,
        string? releaseId,
        long? generation,
        string? connectionGeneration = null)
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            if (_info is null)
            {
                _pendingRuntimeIdentity = new PendingRuntimeIdentity(
                    NormalizeIdentity(buildGitHash),
                    NormalizeIdentity(component),
                    NormalizeIdentity(version),
                    NormalizeIdentity(sourceRevision),
                    NormalizeIdentity(treeHash),
                    NormalizeIdentity(artifactDigest),
                    NormalizeIdentity(releaseId),
                    generation is > 0 ? generation : null,
                    NormalizeIdentity(connectionGeneration));
                _pendingBuildGitHash = _pendingRuntimeIdentity.BuildGitHash;
                return;
            }

            var normalizedConnectionGeneration = NormalizeIdentity(connectionGeneration);
            if (IsStaleConnectionGeneration(_info.ConnectionGeneration, normalizedConnectionGeneration))
                return;

            var next = _info with
            {
                BuildGitHash = NormalizeIdentity(buildGitHash) ?? _info.BuildGitHash,
                Component = NormalizeIdentity(component) ?? _info.Component,
                Version = NormalizeIdentity(version) ?? _info.Version,
                SourceRevision = NormalizeIdentity(sourceRevision) ?? _info.SourceRevision,
                TreeHash = NormalizeIdentity(treeHash) ?? _info.TreeHash,
                ArtifactDigest = NormalizeIdentity(artifactDigest) ?? _info.ArtifactDigest,
                ReleaseId = NormalizeIdentity(releaseId) ?? _info.ReleaseId,
                Generation = generation is > 0 ? generation : _info.Generation,
                ConnectionGeneration = normalizedConnectionGeneration ?? _info.ConnectionGeneration,
            };
            if (Equals(next, _info))
                return;

            SetRunnerInfo(next);
            await PersistAsync();
            if (_status == RunnerStatus.Online)
                await UpsertRegistryAsync();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private static string? NormalizeIdentity(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsStaleConnectionGeneration(string? current, string? incoming)
    {
        if (string.IsNullOrWhiteSpace(current))
            return false;
        if (string.IsNullOrWhiteSpace(incoming))
            return true;
        var currentParts = current.Split(':', 2, StringSplitOptions.None);
        var incomingParts = incoming.Split(':', 2, StringSplitOptions.None);
        if (currentParts.Length == 2
            && incomingParts.Length == 2
            && long.TryParse(currentParts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var currentValue)
            && long.TryParse(incomingParts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var incomingValue))
        {
            return string.Equals(currentParts[0], incomingParts[0], StringComparison.Ordinal)
                && incomingValue < currentValue;
        }
        return !string.Equals(current, incoming, StringComparison.Ordinal);
    }

    private RunnerInfo InfoForRegister(RunnerInfo info)
    {
        return info with
        {
            BuildGitHash = RunnerBuildIdentityPolicy.ResolveForRegister(
                info.BuildGitHash,
                _pendingRuntimeIdentity?.BuildGitHash,
                _pendingBuildGitHash),
            Component = info.Component ?? _pendingRuntimeIdentity?.Component,
            Version = info.Version ?? _pendingRuntimeIdentity?.Version,
            SourceRevision = info.SourceRevision ?? _pendingRuntimeIdentity?.SourceRevision,
            TreeHash = info.TreeHash ?? _pendingRuntimeIdentity?.TreeHash,
            ArtifactDigest = info.ArtifactDigest ?? _pendingRuntimeIdentity?.ArtifactDigest,
            ReleaseId = info.ReleaseId ?? _pendingRuntimeIdentity?.ReleaseId,
            Generation = info.Generation ?? _pendingRuntimeIdentity?.Generation,
            ConnectionGeneration = info.ConnectionGeneration ?? _pendingRuntimeIdentity?.ConnectionGeneration,
            RegisteredAt = info.RegisteredAt ?? _timeProvider.GetUtcNow(),
            ActionCatalog = info.ActionCatalog,
        };
    }

    private RunnerInfo InfoForHeartbeat(RunnerInfo info)
    {
        return info with
        {
            BuildGitHash = RunnerBuildIdentityPolicy.ResolveForHeartbeat(
                info.BuildGitHash,
                _pendingBuildGitHash,
                _info?.BuildGitHash),
            Component = info.Component ?? _info?.Component,
            Version = info.Version ?? _info?.Version,
            SourceRevision = info.SourceRevision ?? _info?.SourceRevision,
            TreeHash = info.TreeHash ?? _info?.TreeHash,
            ArtifactDigest = info.ArtifactDigest ?? _info?.ArtifactDigest,
            ReleaseId = info.ReleaseId ?? _info?.ReleaseId,
            Generation = info.Generation ?? _info?.Generation,
            ConnectionGeneration = info.ConnectionGeneration ?? _info?.ConnectionGeneration,
            RegisteredAt = _info?.RegisteredAt ?? info.RegisteredAt ?? _timeProvider.GetUtcNow(),
            ActionCatalog = info.ActionCatalog,
        };
    }

    private sealed record PendingRuntimeIdentity(
        string? BuildGitHash,
        string? Component,
        string? Version,
        string? SourceRevision,
        string? TreeHash,
        string? ArtifactDigest,
        string? ReleaseId,
        long? Generation,
        string? ConnectionGeneration);
}
