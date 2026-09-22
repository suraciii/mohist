using System.Security.Cryptography;
using System.Text;
using Mohist.Server.Sessions.Grains;

namespace Mohist.Server.Agent.Grains;

public sealed partial class AgentJobGrain
{
    private const string InitialCreating = "creating";
    private const string InitialCandidate = "candidate";
    private const string InitialReady = "ready";
    private const string InitialStarted = "started";

    public async Task<AgentJobInitialRecoveryReceipt> PrepareInitialInputRecoveryAsync(
        PrepareAgentJobInitialRecovery command)
    {
        await HydrateAsync();
        var fingerprint = ValidateInitialClaim(command);
        var current = State.InitialInputSubmission;
        if (current is not null)
        {
            EnsureSameInitialRecovery(current, command, fingerprint);
            return new AgentJobInitialRecoveryReceipt(
                current.Phase,
                current.Phase == InitialCreating
                    && string.Equals(current.CreationAttemptId, command.CreationAttemptId, StringComparison.Ordinal),
                current.ReplacementRuntime,
                current.ReplacementRuntimeSessionId);
        }

        State.InitialInputSubmission = new AgentJobInitialInputSubmission(
            command.OperationId,
            command.WorkId,
            command.ProcessGeneration,
            command.RunnerId,
            command.SessionId,
            command.InputId,
            command.TurnId,
            command.ExpectedRuntime,
            command.ExpectedRuntimeSessionId,
            fingerprint,
            InitialCreating,
            _timeProvider.GetUtcNow(),
            CreationAttemptId: command.CreationAttemptId);
        await PersistAsync();
        return new AgentJobInitialRecoveryReceipt(InitialCreating, true, null, null);
    }

    public async Task<AgentJobInitialRecoveryReceipt> CompleteInitialInputRecoveryAsync(
        CompleteAgentJobInitialRecovery command)
    {
        await HydrateAsync();
        var fingerprint = ValidateInitialClaim(command.Recovery, allowRetargetedBinding: true);
        var current = State.InitialInputSubmission
            ?? throw new InvalidOperationException("initial_input_recovery_not_prepared");
        EnsureSameInitialRecovery(current, command.Recovery, fingerprint);
        if (string.IsNullOrWhiteSpace(command.ReplacementRuntime)
            || string.IsNullOrWhiteSpace(command.ReplacementRuntimeSessionId))
            throw new InvalidOperationException("initial_input_replacement_incomplete");
        if (current.ReplacementRuntime is not null
            && (!string.Equals(current.ReplacementRuntime, command.ReplacementRuntime, StringComparison.Ordinal)
                || !string.Equals(current.ReplacementRuntimeSessionId, command.ReplacementRuntimeSessionId, StringComparison.Ordinal)))
            throw new InvalidOperationException("initial_input_candidate_changed");
        if (current.Phase == InitialStarted)
            throw new InvalidOperationException("initial_input_already_started");

        if (current.Phase == InitialCreating)
        {
            current = current with
            {
                Phase = InitialCandidate,
                ReplacementRuntime = command.ReplacementRuntime,
                ReplacementRuntimeSessionId = command.ReplacementRuntimeSessionId,
            };
            State.InitialInputSubmission = current;
            await PersistAsync();
        }

        if (current.Phase == InitialReady)
        {
            await PersistAsync();
            return new AgentJobInitialRecoveryReceipt(
                current.Phase, false, current.ReplacementRuntime, current.ReplacementRuntimeSessionId);
        }

        var session = _grains.GetGrain<IAgentSessionGrain>(command.Recovery.SessionId);
        var snapshot = await session.GetAsync()
            ?? throw new InvalidOperationException("initial_input_session_missing");
        var receipt = await session.RecoverInitialAgentJobRuntimeSessionAsync(
            new RecoverInitialAgentJobRuntimeSessionCommand(
                command.Recovery.OperationId,
                Key,
                command.Recovery.WorkId,
                command.Recovery.ProcessGeneration,
                command.Recovery.RunnerId,
                command.Recovery.InputId,
                command.Recovery.TurnId,
                command.Recovery.ExpectedRuntime,
                command.Recovery.ExpectedRuntimeSessionId,
                command.ReplacementRuntime,
                command.ReplacementRuntimeSessionId,
                snapshot.BindingEpoch));

        State.RuntimeSessionId = receipt.RuntimeSessionId;
        State.InitialInputSubmission = current with
        {
            Phase = InitialReady,
            ReplacementRuntime = receipt.Runtime,
            ReplacementRuntimeSessionId = receipt.RuntimeSessionId,
            BindingEpoch = receipt.BindingEpoch,
            ContextGeneration = receipt.ContextGeneration,
        };
        await PersistAsync();
        return new AgentJobInitialRecoveryReceipt(InitialReady, false, receipt.Runtime, receipt.RuntimeSessionId);
    }

    public async Task<AgentJobInitialStartReceipt> StartInitialInputAsync(StartAgentJobInitialInput command)
    {
        await HydrateAsync();
        ValidateInitialStartClaim(command);
        var session = _grains.GetGrain<IAgentSessionGrain>(command.SessionId);
        var snapshot = await session.GetAsync()
            ?? throw new InvalidOperationException("initial_input_session_missing");
        if (!string.Equals(snapshot.RunnerId, command.RunnerId, StringComparison.Ordinal)
            || !string.Equals(snapshot.Runtime, command.Runtime, StringComparison.Ordinal)
            || !string.Equals(snapshot.AgentSessionId, command.RuntimeSessionId, StringComparison.Ordinal))
            throw new InvalidOperationException("initial_input_start_binding_mismatch");

        var current = State.InitialInputSubmission;
        if (current is null)
        {
            current = new AgentJobInitialInputSubmission(
                command.OperationId,
                command.WorkId,
                command.ProcessGeneration,
                command.RunnerId,
                command.SessionId,
                command.InputId,
                command.TurnId,
                command.Runtime,
                command.RuntimeSessionId,
                CurrentDispatchFingerprint(),
                InitialReady,
                _timeProvider.GetUtcNow(),
                command.Runtime,
                command.RuntimeSessionId,
                snapshot.BindingEpoch,
                snapshot.ContextGeneration);
            State.InitialInputSubmission = current;
            await PersistAsync();
        }
        else
        {
            if (!MatchesStart(current, command))
                throw new InvalidOperationException("initial_input_start_operation_mismatch");
            if (current.Phase == InitialStarted)
            {
                await PersistAsync();
                var sameAttempt = string.Equals(
                    current.SubmissionAttemptId,
                    command.SubmissionAttemptId,
                    StringComparison.Ordinal);
                return new AgentJobInitialStartReceipt(
                    true,
                    sameAttempt,
                    current.ReplacementRuntime,
                    current.ReplacementRuntimeSessionId);
            }
            if (current.Phase != InitialReady)
                throw new InvalidOperationException("initial_input_recovery_not_ready");
        }

        var admitted = await session.AdmitInitialAgentJobInputAsync(new AdmitInitialAgentJobInputCommand(
            command.OperationId,
            Key,
            command.WorkId,
            command.ProcessGeneration,
            command.RunnerId,
            command.InputId,
            command.TurnId,
            command.Runtime,
            command.RuntimeSessionId,
            snapshot.BindingEpoch,
            snapshot.ContextGeneration));
        if (!admitted.EffectAdmitted)
            throw new InvalidOperationException("initial_input_effect_not_admitted");

        State.InitialInputSubmission = current with
        {
            Phase = InitialStarted,
            ReplacementRuntime = admitted.Runtime,
            ReplacementRuntimeSessionId = admitted.RuntimeSessionId,
            BindingEpoch = admitted.BindingEpoch,
            ContextGeneration = admitted.ContextGeneration,
            SubmissionAttemptId = command.SubmissionAttemptId,
            StartedAt = _timeProvider.GetUtcNow(),
        };
        await PersistAsync();
        return new AgentJobInitialStartReceipt(true, true, admitted.Runtime, admitted.RuntimeSessionId);
    }

    private string ValidateInitialClaim(
        PrepareAgentJobInitialRecovery command,
        bool allowRetargetedBinding = false)
    {
        if (string.IsNullOrWhiteSpace(command.CreationAttemptId)
            || State.Status != AgentJobStatus.Running
            || !string.Equals(State.RunnerId, command.RunnerId, StringComparison.Ordinal)
            || !string.Equals(State.WorkId, command.WorkId, StringComparison.Ordinal)
            || !string.Equals(_ledger?.ClaimedProcessGeneration, command.ProcessGeneration, StringComparison.Ordinal)
            || !string.Equals(State.Input?.AgentSessionId, command.SessionId, StringComparison.Ordinal)
            || !string.Equals(State.Input?.InitialInputId, command.InputId, StringComparison.Ordinal)
            || !string.Equals(State.Input?.InitialTurnId, command.TurnId, StringComparison.Ordinal)
            || !string.Equals(ExecutionDefinitionFrom(State.Input)?.Runtime, command.ExpectedRuntime, StringComparison.Ordinal)
            || (!allowRetargetedBinding
                && !string.Equals(State.RuntimeSessionId, command.ExpectedRuntimeSessionId, StringComparison.Ordinal)))
            throw new InvalidOperationException("initial_input_claim_mismatch");
        return CurrentDispatchFingerprint();
    }

    private void ValidateInitialStartClaim(StartAgentJobInitialInput command)
    {
        if (string.IsNullOrWhiteSpace(command.OperationId)
            || string.IsNullOrWhiteSpace(command.SubmissionAttemptId)
            || State.Status != AgentJobStatus.Running
            || !string.Equals(State.RunnerId, command.RunnerId, StringComparison.Ordinal)
            || !string.Equals(State.WorkId, command.WorkId, StringComparison.Ordinal)
            || !string.Equals(_ledger?.ClaimedProcessGeneration, command.ProcessGeneration, StringComparison.Ordinal)
            || !string.Equals(State.Input?.AgentSessionId, command.SessionId, StringComparison.Ordinal)
            || !string.Equals(State.Input?.InitialInputId, command.InputId, StringComparison.Ordinal)
            || !string.Equals(State.Input?.InitialTurnId, command.TurnId, StringComparison.Ordinal)
            || !string.Equals(
                State.InitialInputSubmission?.ReplacementRuntime ?? ExecutionDefinitionFrom(State.Input)?.Runtime,
                command.Runtime,
                StringComparison.Ordinal)
            || !string.Equals(State.RuntimeSessionId, command.RuntimeSessionId, StringComparison.Ordinal))
            throw new InvalidOperationException("initial_input_start_claim_mismatch");
    }

    private string CurrentDispatchFingerprint()
    {
        if (string.IsNullOrWhiteSpace(_ledger?.DispatchJson))
            throw new InvalidOperationException("initial_input_dispatch_missing");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_ledger.DispatchJson)));
    }

    private static void EnsureSameInitialRecovery(
        AgentJobInitialInputSubmission current,
        PrepareAgentJobInitialRecovery command,
        string dispatchFingerprint)
    {
        if (!string.Equals(current.OperationId, command.OperationId, StringComparison.Ordinal)
            || !string.Equals(current.WorkId, command.WorkId, StringComparison.Ordinal)
            || !string.Equals(current.ProcessGeneration, command.ProcessGeneration, StringComparison.Ordinal)
            || !string.Equals(current.RunnerId, command.RunnerId, StringComparison.Ordinal)
            || !string.Equals(current.SessionId, command.SessionId, StringComparison.Ordinal)
            || !string.Equals(current.InputId, command.InputId, StringComparison.Ordinal)
            || !string.Equals(current.TurnId, command.TurnId, StringComparison.Ordinal)
            || !string.Equals(current.ExpectedRuntime, command.ExpectedRuntime, StringComparison.Ordinal)
            || !string.Equals(current.ExpectedRuntimeSessionId, command.ExpectedRuntimeSessionId, StringComparison.Ordinal)
            || !string.Equals(current.CreationAttemptId, command.CreationAttemptId, StringComparison.Ordinal)
            || !string.Equals(current.DispatchFingerprint, dispatchFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("initial_input_recovery_operation_mismatch");
    }

    private static bool MatchesStart(AgentJobInitialInputSubmission current, StartAgentJobInitialInput command) =>
        string.Equals(current.OperationId, command.OperationId, StringComparison.Ordinal)
        && string.Equals(current.WorkId, command.WorkId, StringComparison.Ordinal)
        && string.Equals(current.ProcessGeneration, command.ProcessGeneration, StringComparison.Ordinal)
        && string.Equals(current.RunnerId, command.RunnerId, StringComparison.Ordinal)
        && string.Equals(current.SessionId, command.SessionId, StringComparison.Ordinal)
        && string.Equals(current.InputId, command.InputId, StringComparison.Ordinal)
        && string.Equals(current.TurnId, command.TurnId, StringComparison.Ordinal)
        && string.Equals(current.ReplacementRuntime, command.Runtime, StringComparison.Ordinal)
        && string.Equals(current.ReplacementRuntimeSessionId, command.RuntimeSessionId, StringComparison.Ordinal);
}
