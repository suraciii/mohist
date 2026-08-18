namespace Mohist.Server.Sessions.Domain;

public static partial class AgentSessionExtensions
{
    extension(AgentSession session)
    {
        public AgentRuntimeBinding CurrentRuntimeBinding() =>
            new(session.Runtime.RunnerId, NormalizeRuntime(session.Runtime.Runtime), session.Status.AgentRuntimeSessionId);

        private static void EnsureExpectedRuntimeBinding(
            AgentSession actualSession,
            AgentRuntimeBinding expected,
            AgentRuntimeBinding actual)
        {
            if (expected == actual) return;
            throw new StaleRuntimeSessionBindingException(actualSession.Id, expected.RuntimeSessionId, actual.RuntimeSessionId);
        }

        private static bool HasHeldBindingUse(AgentSession currentSession) =>
            currentSession.BindingUseReceipts?.Any(item => item.State == SessionTreeBindingUseState.Held) == true;
    }
}
