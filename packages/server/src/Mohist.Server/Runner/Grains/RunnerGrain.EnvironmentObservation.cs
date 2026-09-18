namespace Mohist.Server.Runner.Grains;

public partial class RunnerGrain
{
    private const int MaxEnvironmentObservationTextLength = 256;
    private const int MaxEnvironmentObservationPathLength = 512;
    private const int MaxEnvironmentObservationList = 32;
    private const int MaxEnvironmentToolChecks = 8;

    public async Task<RunnerEnvironmentObservationResult> RecordEnvironmentObservationAsync(
        RunnerEnvironmentObservation observation)
    {
        if (observation is null)
            throw new ArgumentException("environment observation is required", nameof(observation));

        var normalized = NormalizeEnvironmentObservation(observation);
        await _lifecycleGate.WaitAsync();
        try
        {
            if (!string.IsNullOrWhiteSpace(normalized.ProcessGeneration)
                && !string.Equals(
                    _state.State?.CurrentProcessGeneration,
                    normalized.ProcessGeneration,
                    StringComparison.Ordinal))
            {
                return new(
                    RunnerEnvironmentObservationStatus.Stale,
                    CloneEnvironmentObservation(_state.State?.EnvironmentObservation));
            }

            var state = _state.State ??= new RunnerState();
            state.EnvironmentObservation = MergeEnvironmentObservation(
                state.EnvironmentObservation,
                normalized,
                _timeProvider.GetUtcNow());
            await PersistAsync();
            PublishStatusObservation();
            return new(
                RunnerEnvironmentObservationStatus.Accepted,
                CloneEnvironmentObservation(state.EnvironmentObservation));
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private static RunnerEnvironmentObservation NormalizeEnvironmentObservation(
        RunnerEnvironmentObservation value)
    {
        var processGeneration = NormalizeObservationText(value.ProcessGeneration);
        var environmentVersion = NormalizeObservationText(value.EnvironmentVersion);
        if (environmentVersion is not null && processGeneration is null)
            throw new ArgumentException("environment version requires process generation", nameof(value));

        var candidateVersion = NormalizeObservationText(value.CandidateVersion);
        var candidateSource = NormalizeObservationText(value.CandidateSource);
        var candidateUser = NormalizeObservationText(value.CandidateUser);
        var candidateVariables = NormalizeObservationNames(value.CandidateVariables, nameof(value.CandidateVariables));
        var candidateAdded = NormalizeObservationNames(value.CandidateAddedVariables, nameof(value.CandidateAddedVariables));
        var candidateRemoved = NormalizeObservationNames(value.CandidateRemovedVariables, nameof(value.CandidateRemovedVariables));
        var candidateChanged = NormalizeObservationNames(value.CandidateChangedVariables, nameof(value.CandidateChangedVariables));
        var checks = (value.ToolChecks ?? [])
            .Select(NormalizeToolCheck)
            .ToArray();
        if (checks.Length > MaxEnvironmentToolChecks)
            throw new ArgumentException("environment observation has too many tool checks", nameof(value.ToolChecks));

        if (candidateVersion is null
            && candidateSource is null
            && candidateUser is null
            && candidateVariables.Length == 0
            && candidateAdded.Length == 0
            && candidateRemoved.Length == 0
            && candidateChanged.Length == 0
            && checks.Length == 0
            && processGeneration is null)
            throw new ArgumentException("environment observation has no reportable facts", nameof(value));

        return new RunnerEnvironmentObservation
        {
            ProcessGeneration = processGeneration,
            EnvironmentVersion = environmentVersion,
            EnvironmentLoadedAt = value.EnvironmentLoadedAt,
            CandidateSource = candidateSource,
            CandidateUser = candidateUser,
            CandidateVersion = candidateVersion,
            CandidateVariables = candidateVariables,
            CandidateCapturedAt = value.CandidateCapturedAt,
            CandidateAddedVariables = candidateAdded,
            CandidateRemovedVariables = candidateRemoved,
            CandidateChangedVariables = candidateChanged,
            ToolChecks = checks,
            ReportedAt = value.ReportedAt,
        };
    }

    private static RunnerEnvironmentToolCheck NormalizeToolCheck(RunnerEnvironmentToolCheck value)
    {
        if (value is null)
            throw new ArgumentException("tool check is required", nameof(value));

        var executable = NormalizeObservationText(value.Executable)
            ?? throw new ArgumentException("tool check executable is required", nameof(value));
        if (executable.Length > 128)
            throw new ArgumentException("tool check executable is too long", nameof(value));

        var snapshotKind = NormalizeObservationText(value.SnapshotKind);
        if (snapshotKind is not ("active" or "candidate"))
            throw new ArgumentException("tool check snapshot kind is invalid", nameof(value));

        var outcome = NormalizeObservationText(value.Outcome);
        if (outcome is not ("passed" or "failed" or "not-found" or "timed-out" or "error"))
            throw new ArgumentException("tool check outcome is invalid", nameof(value));

        var path = NormalizeObservationPath(value.ResolvedPath);
        if (value.ExitCode is < 0 or > 255)
            throw new ArgumentException("tool check exit code is invalid", nameof(value));
        if (value.DurationMilliseconds is < 0 or > 10_000)
            throw new ArgumentException("tool check duration is invalid", nameof(value));

        return new RunnerEnvironmentToolCheck
        {
            Executable = executable,
            ResolvedPath = path,
            SnapshotKind = snapshotKind,
            SnapshotVersion = NormalizeObservationText(value.SnapshotVersion),
            Outcome = outcome,
            ExitCode = value.ExitCode,
            DurationMilliseconds = value.DurationMilliseconds,
            CheckedAt = value.CheckedAt,
        };
    }

    private static RunnerEnvironmentObservation MergeEnvironmentObservation(
        RunnerEnvironmentObservation? previous,
        RunnerEnvironmentObservation incoming,
        DateTimeOffset reportedAt)
    {
        var result = CloneEnvironmentObservation(previous) ?? new RunnerEnvironmentObservation();
        if (incoming.ProcessGeneration is not null)
            result.ProcessGeneration = incoming.ProcessGeneration;
        if (incoming.EnvironmentVersion is not null)
        {
            result.EnvironmentVersion = incoming.EnvironmentVersion;
            result.EnvironmentLoadedAt = incoming.EnvironmentLoadedAt;
        }

        var hasCandidate = incoming.CandidateVersion is not null
            || incoming.CandidateSource is not null
            || incoming.CandidateUser is not null
            || incoming.CandidateVariables.Length > 0
            || incoming.CandidateAddedVariables.Length > 0
            || incoming.CandidateRemovedVariables.Length > 0
            || incoming.CandidateChangedVariables.Length > 0;
        if (hasCandidate)
        {
            result.CandidateSource = incoming.CandidateSource;
            result.CandidateUser = incoming.CandidateUser;
            result.CandidateVersion = incoming.CandidateVersion;
            result.CandidateVariables = incoming.CandidateVariables;
            result.CandidateCapturedAt = incoming.CandidateCapturedAt;
            result.CandidateAddedVariables = incoming.CandidateAddedVariables;
            result.CandidateRemovedVariables = incoming.CandidateRemovedVariables;
            result.CandidateChangedVariables = incoming.CandidateChangedVariables;
        }

        if (incoming.ToolChecks.Length > 0)
        {
            result.ToolChecks = result.ToolChecks
                .Concat(incoming.ToolChecks)
                .TakeLast(MaxEnvironmentToolChecks)
                .Select(CloneToolCheck)
                .ToArray();
        }

        result.ReportedAt = reportedAt;
        return result;
    }

    private static RunnerEnvironmentObservation? CloneEnvironmentObservation(
        RunnerEnvironmentObservation? value)
    {
        if (value is null)
            return null;

        return new RunnerEnvironmentObservation
        {
            ProcessGeneration = value.ProcessGeneration,
            EnvironmentVersion = value.EnvironmentVersion,
            EnvironmentLoadedAt = value.EnvironmentLoadedAt,
            CandidateSource = value.CandidateSource,
            CandidateUser = value.CandidateUser,
            CandidateVersion = value.CandidateVersion,
            CandidateVariables = [.. value.CandidateVariables ?? []],
            CandidateCapturedAt = value.CandidateCapturedAt,
            CandidateAddedVariables = [.. value.CandidateAddedVariables ?? []],
            CandidateRemovedVariables = [.. value.CandidateRemovedVariables ?? []],
            CandidateChangedVariables = [.. value.CandidateChangedVariables ?? []],
            ToolChecks = (value.ToolChecks ?? []).Select(CloneToolCheck).ToArray(),
            ReportedAt = value.ReportedAt,
        };
    }

    private static RunnerEnvironmentToolCheck CloneToolCheck(RunnerEnvironmentToolCheck value) =>
        new()
        {
            Executable = value.Executable,
            ResolvedPath = value.ResolvedPath,
            SnapshotKind = value.SnapshotKind,
            SnapshotVersion = value.SnapshotVersion,
            Outcome = value.Outcome,
            ExitCode = value.ExitCode,
            DurationMilliseconds = value.DurationMilliseconds,
            CheckedAt = value.CheckedAt,
        };

    private static string? NormalizeObservationText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var normalized = value.Trim();
        return normalized.Length <= MaxEnvironmentObservationTextLength
            && !normalized.Any(char.IsControl)
            ? normalized
            : throw new ArgumentException("environment observation text is invalid");
    }

    private static string? NormalizeObservationPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var normalized = value.Trim();
        if (normalized.Length > MaxEnvironmentObservationPathLength
            || normalized.Any(char.IsControl)
            || !Path.IsPathFullyQualified(normalized))
            throw new ArgumentException("tool check resolved path is invalid");
        return normalized;
    }

    private static string[] NormalizeObservationNames(string[]? values, string parameterName)
    {
        var normalized = (values ?? [])
            .Select(value => NormalizeObservationText(value)
                ?? throw new ArgumentException("environment variable name is required", parameterName))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (normalized.Length > MaxEnvironmentObservationList
            || normalized.Any(value => value.Length > 128 || value.Any(character =>
                !(char.IsLetterOrDigit(character) || character is '_' or '.' or '-'))))
            throw new ArgumentException("environment variable names are invalid", parameterName);
        return normalized;
    }
}
