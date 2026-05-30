namespace Mohist.Server.Sessions.Domain;

public static partial class WorkflowAgentSessionExtensions
{
    extension(WorkflowAgentSession session)
    {
        public bool IsTerminal => session.Status is AgentSessionStatus.Completed or AgentSessionStatus.Failed or AgentSessionStatus.Cancelled;

        public void Start(string? model, DateTime now)
        {
            if (session.IsTerminal) return;
            session.Status = AgentSessionStatus.Running;
            session.Model = model ?? session.Model;
            session.StartedAt ??= now;
            session.LastDataAt = now;
            session.LastHeartbeatAt = now;
        }

        public void RecordActivity(DateTime now)
        {
            if (session.IsTerminal) return;
            session.LastDataAt = now;
            session.LastHeartbeatAt = now;
        }

        public void MarkActive(string status, DateTime now, string? failureReason = null)
        {
            if (session.IsTerminal) return;
            session.Status = AgentSessionStatusNames.ParseActive(status);
            session.LastDataAt = now;
            session.LastHeartbeatAt = now;
            session.FailureReason = failureReason ?? session.FailureReason;
        }

        public void Complete(DateTime now, int? exitCode)
        {
            if (session.IsTerminal) return;
            session.Status = AgentSessionStatus.Completed;
            session.CompletedAt = now;
            session.LastDataAt = now;
            session.LastHeartbeatAt = now;
            session.ExitCode = exitCode ?? session.ExitCode;
        }

        public void Fail(DateTime now, string? reason, int? exitCode = null)
        {
            if (session.IsTerminal) return;
            session.Status = AgentSessionStatus.Failed;
            session.CompletedAt = now;
            session.LastDataAt = now;
            session.LastHeartbeatAt = now;
            session.FailureReason = reason ?? session.FailureReason;
            session.ExitCode = exitCode ?? session.ExitCode;
        }

        public void Cancel(DateTime now, string? reason, int? exitCode = null)
        {
            if (session.IsTerminal) return;
            session.Status = AgentSessionStatus.Cancelled;
            session.CompletedAt = now;
            session.LastDataAt = now;
            session.LastHeartbeatAt = now;
            session.FailureReason = reason ?? session.FailureReason;
            session.ExitCode = exitCode ?? session.ExitCode;
        }
    }
}
