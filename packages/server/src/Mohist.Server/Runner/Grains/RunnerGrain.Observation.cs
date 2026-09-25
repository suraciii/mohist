using Mohist.Server.Runner.Services;

namespace Mohist.Server.Runner.Grains;

public partial class RunnerGrain
{
    public async Task<RunnerDispatchObservation?> ObserveDispatchObservationAsync(
        string processGeneration,
        RunnerDispatchObservation observation)
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            if (_status != RunnerStatus.Online
                || _info is null
                || !string.Equals(_state.State?.CurrentProcessGeneration, processGeneration, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(observation.ConnectionGeneration)
                || !string.Equals(_info.ConnectionGeneration, observation.ConnectionGeneration, StringComparison.Ordinal))
                return null;

            var normalizedConnectionGeneration = observation.ConnectionGeneration.Trim();
            var suppliedReasons = observation.AdmissionReasonCodes ?? [];
            var knownReasons = RunnerAdmissionReasonCodes.Local
                .Where(suppliedReasons.Contains)
                .OrderBy(reason => reason, StringComparer.Ordinal)
                .ToList();
            var reasonsAreValid = suppliedReasons.Count == knownReasons.Count
                && suppliedReasons.Distinct(StringComparer.Ordinal).Count() == suppliedReasons.Count;
            var admissionIsConsistent = observation.AdmissionReady
                ? reasonsAreValid && suppliedReasons.Count == 0
                : reasonsAreValid && suppliedReasons.Count > 0;
            if (!admissionIsConsistent)
            {
                knownReasons = [RunnerAdmissionReasonCodes.ObservationInvalid];
                observation = observation with
                {
                    AdmissionReady = false,
                    AdmissionReasonCodes = knownReasons,
                };
            }

            var currentWitnesses = string.Equals(
                    _dispatchObservation?.ConnectionGeneration,
                    normalizedConnectionGeneration,
                    StringComparison.Ordinal)
                ? _dispatchObservation?.RuntimeReadiness.ToDictionary(
                    witness => witness.Runtime,
                    StringComparer.OrdinalIgnoreCase)
                    ?? new Dictionary<string, RuntimeReadinessWitness>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, RuntimeReadinessWitness>(StringComparer.OrdinalIgnoreCase);
            var witnesses = new Dictionary<string, RuntimeReadinessWitness>(StringComparer.OrdinalIgnoreCase);
            foreach (var witness in observation.RuntimeReadiness ?? [])
            {
                var runtime = witness.Runtime?.Trim();
                if (string.IsNullOrWhiteSpace(runtime) || witness.Generation is not > 0)
                    continue;

                if (currentWitnesses.TryGetValue(runtime, out var previous)
                    && previous.Generation is { } previousGeneration
                    && witness.Generation is { } incomingGeneration
                    && previousGeneration > incomingGeneration)
                {
                    witnesses[runtime] = previous;
                    continue;
                }

                witnesses[runtime] = witness with { Runtime = runtime };
            }

            // A poll that names a work key is the only thing that renews that
            // work's confirmation. The renewal time is this poll's receipt time,
            // so a continuing heartbeat alone never keeps a work item confirmed.
            var previousConfirmations = string.Equals(
                    _dispatchObservation?.ConnectionGeneration,
                    normalizedConnectionGeneration,
                    StringComparison.Ordinal)
                ? _dispatchObservation?.WorkConfirmations
                : null;
            var workConfirmations = RunnerWorkConfirmationLedger.Renew(
                previousConfirmations,
                observation.ReportedWorkKeys,
                processGeneration.Trim(),
                _timeProvider.GetUtcNow());

            _dispatchObservation = new RunnerDispatchObservation(
                normalizedConnectionGeneration,
                observation.AdmissionReady,
                [.. knownReasons],
                [.. witnesses.Values],
                processGeneration.Trim(),
                Math.Max(0, observation.InFlightCount),
                Math.Max(0, observation.AwaitingAckCount),
                observation.ReportedWorkKeys,
                [.. workConfirmations]);
            PublishStatusObservation();

            return CloneDispatchObservation(_dispatchObservation);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private IReadOnlyDictionary<string, RuntimeReadinessWitness> RuntimeReadinessByName() =>
        _dispatchObservation?.RuntimeReadiness.ToDictionary(
            witness => witness.Runtime,
            StringComparer.OrdinalIgnoreCase)
        ?? new Dictionary<string, RuntimeReadinessWitness>(StringComparer.OrdinalIgnoreCase);

    private static RunnerDispatchObservation? CloneDispatchObservation(RunnerDispatchObservation? observation)
    {
        if (observation is null)
            return null;

        return new RunnerDispatchObservation(
            observation.ConnectionGeneration,
            observation.AdmissionReady,
            [.. observation.AdmissionReasonCodes],
            observation.RuntimeReadiness
                .Select(witness => witness with { })
                .ToList(),
            observation.ProcessGeneration,
            observation.InFlightCount,
            observation.AwaitingAckCount,
            observation.ReportedWorkKeys is null ? null : [.. observation.ReportedWorkKeys],
            observation.WorkConfirmations is null ? null : [.. observation.WorkConfirmations]);
    }
}
