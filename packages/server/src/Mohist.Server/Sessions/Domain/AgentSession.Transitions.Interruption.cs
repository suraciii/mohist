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
                var transitionRank = AgentWorkInterruptionProjection.Rank(transition.State);
                var currentRank = current is null
                    ? 0
                    : AgentWorkInterruptionProjection.Rank(current.State);
                if (current is not null
                    && (currentRank > transitionRank
                        || (currentRank == transitionRank
                            && (string.IsNullOrWhiteSpace(transition.StopFailure)
                                || string.Equals(current.StopFailure, transition.StopFailure, StringComparison.Ordinal)))))
                    continue;
                turns[index] = turn with { Interruption = transition };
                turnChanged = true;
            }

            var historyChanged = prior is null
                || AgentWorkInterruptionProjection.Rank(transition.State)
                    > AgentWorkInterruptionProjection.Rank(prior.State)
                || (prior is not null
                    && !string.IsNullOrWhiteSpace(transition.StopFailure)
                    && !string.Equals(prior.StopFailure, transition.StopFailure, StringComparison.Ordinal));
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
