using Mohist.Server.Contracts;
using Mohist.Server.Sessions.Domain;

namespace Mohist.Server.Sessions.Grains;

public sealed partial class AgentSessionGrain
{
    public async Task<InitialAgentJobSessionReceipt> RecoverInitialAgentJobRuntimeSessionAsync(
        RecoverInitialAgentJobRuntimeSessionCommand command)
    {
        var session = await GetRequiredAsync();
        var now = Now();
        var operation = new AgentInitialInputOperation(
            command.OperationId,
            command.JobId,
            command.WorkId,
            command.ProcessGeneration,
            command.RunnerId,
            command.InputId,
            command.TurnId,
            command.ReplacementRuntime,
            command.ReplacementRuntimeSessionId,
            session.BindingEpoch,
            session.Status.ContextGeneration,
            now);
        var events = session.RecoverInitialAgentJobTurn(
            new AgentRuntimeBinding(command.RunnerId, command.ExpectedRuntime, command.ExpectedRuntimeSessionId),
            new AgentRuntimeBinding(command.RunnerId, command.ReplacementRuntime, command.ReplacementRuntimeSessionId),
            operation,
            now,
            command.ExpectedBindingEpoch);
        if (events.Count > 0)
            await PersistRecoveryAsync(session, events, BuildContextResetTranscriptEntries(session, "missing-recovery", now));
        else
        {
            await _stateStore.SaveAsync(SessionId, session);
            _session = session;
        }
        var receipt = session.Status.InitialInputOperation
            ?? throw new InvalidOperationException("initial_input_recovery_receipt_missing");
        return ToInitialReceipt(receipt);
    }

    public async Task<InitialAgentJobSessionReceipt> AdmitInitialAgentJobInputAsync(
        AdmitInitialAgentJobInputCommand command)
    {
        var session = await GetRequiredAsync();
        var now = Now();
        var operation = new AgentInitialInputOperation(
            command.OperationId,
            command.JobId,
            command.WorkId,
            command.ProcessGeneration,
            command.RunnerId,
            command.InputId,
            command.TurnId,
            command.Runtime,
            command.RuntimeSessionId,
            command.BindingEpoch,
            command.ContextGeneration,
            now,
            SubmissionAttemptId: command.SubmissionAttemptId);
        session.AdmitInitialAgentJobInputEffect(operation, now);
        await _stateStore.SaveAsync(SessionId, session);
        _session = session;
        return ToInitialReceipt(session.Status.InitialInputOperation!);
    }

    private static InitialAgentJobSessionReceipt ToInitialReceipt(AgentInitialInputOperation operation) =>
        new(
            operation.OperationId,
            operation.Runtime,
            operation.RuntimeSessionId,
            operation.BindingEpoch,
            operation.ContextGeneration,
            operation.EffectAdmitted,
            operation.SubmissionAttemptId);

}
