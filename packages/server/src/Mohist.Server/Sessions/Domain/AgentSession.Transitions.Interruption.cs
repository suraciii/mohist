using Mohist.Server.Contracts;

namespace Mohist.Server.Sessions.Domain;

public static partial class AgentSessionExtensions
{
    extension(AgentSession session)
    {
        public IReadOnlyList<AgentSessionEvent> ApplyInterruption(
            AgentWorkInterruptionTransition transition,
            DateTime now)
        {
            var prior = (session.Status.InterruptionHistory ?? [])
                .SingleOrDefault(item =>
                    string.Equals(item.WorkId, transition.WorkId, StringComparison.Ordinal)
                    && item.RecoveryGeneration == transition.RecoveryGeneration);
            var projected = AgentWorkInterruptionProjection.Apply(
                session.Status.InterruptionHistory,
                transition);
            var turns = (session.Status.Turns ?? []).ToList();
            var turnChanged = false;
            for (var index = 0; index < turns.Count; index++)
            {
                var turn = turns[index];
                var isOriginal = string.Equals(turn.Id, transition.OriginalTurnId, StringComparison.Ordinal);
                var isReplacement = string.Equals(turn.Id, transition.ReplacementTurnId, StringComparison.Ordinal);
                if (!isOriginal && !isReplacement) continue;

                if (isOriginal && transition.RecoveryGeneration > 0)
                    continue;

                var current = turn.Interruption;
                if (current is not null
                    && AgentWorkInterruptionProjection.Rank(current.State)
                        >= AgentWorkInterruptionProjection.Rank(transition.State))
                    continue;
                turns[index] = turn with { Interruption = transition };
                turnChanged = true;
            }

            var historyChanged = prior is null
                || AgentWorkInterruptionProjection.Rank(transition.State)
                    > AgentWorkInterruptionProjection.Rank(prior.State);
            if (!historyChanged && !turnChanged)
                return [];

            session.Status = session.Status with
            {
                Turns = turns,
                InterruptionHistory = projected,
                LastDataAt = now,
            };
            return [new AgentSessionInterruptionLifecycleChanged(transition)];
        }
    }
}
