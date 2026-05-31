namespace Mohist.Server.Sessions.Domain;

public static partial class WorkflowAgentSessionExtensions
{
    extension(WorkflowAgentSession session)
    {
        public bool IsTerminal => session.Status is AgentSessionStatus.Completed or AgentSessionStatus.Failed or AgentSessionStatus.Cancelled;

        public bool IsCreated => session.Status == AgentSessionStatus.Created;

        public void MergeContext(
            string? runnerId,
            string? workId,
            string? workType,
            string? stage,
            string? title,
            int? issueNumber)
        {
            session.RunnerId ??= runnerId;
            session.WorkId ??= workId;
            session.WorkType ??= workType;
            session.Stage ??= stage;
            session.Title ??= title;
            if (session.IssueNumber == 0 && issueNumber is > 0)
                session.IssueNumber = issueNumber.Value;
        }

        public void StartNewWork(
            string? runnerId,
            string? workId,
            string? workType,
            string? stage,
            string? title,
            int? issueNumber,
            DateTime now)
        {
            session.RunnerId = runnerId ?? session.RunnerId;
            session.WorkId = workId;
            session.WorkType = workType;
            session.Stage = stage;
            session.Title = title;
            if (session.IssueNumber == 0 && issueNumber is > 0)
                session.IssueNumber = issueNumber.Value;

            session.Status = AgentSessionStatus.Created;
            session.StartedAt = null;
            session.CompletedAt = null;
            session.FailureReason = null;
            session.ExitCode = null;
            session.LastDataAt = now;
            session.LastHeartbeatAt = now;
        }

        public bool AttachAgent(
            string agentSessionId,
            string? model,
            string? workDir,
            string? changeDir,
            int? processPid,
            DateTime now)
        {
            if (session.IsTerminal) return false;

            session.AgentSessionId = agentSessionId;
            session.Model = model ?? session.Model;
            session.WorkDir = workDir ?? session.WorkDir;
            session.ChangeDir = changeDir ?? session.ChangeDir;
            session.ProcessPid = processPid ?? session.ProcessPid;
            session.Start(model, now);
            return true;
        }

        public void EnsureActive(DateTime now)
        {
            if (session.IsCreated)
                session.MarkActive("running", now);
        }

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
