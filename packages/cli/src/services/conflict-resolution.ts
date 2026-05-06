import { buildConflictResolutionPrompt } from '../agents/artifact-prompt';
import { createAcpConnection, type AgentSessionOptions } from '../agent-runtime/acp-session';
import type { IssueRepo } from '../db/issue-repo';
import type { WorkflowLogRepo } from '../db/workflow-log-repo';
import type { SessionStreamLogRepo } from '../db/session-stream-log-repo';
import type { CoderSessionRepo } from '../db/coder-session-repo';
import type { EventBus } from './event-bus';
import { loadAgentConfig } from '../workflow/workflow-loader';

export interface ConflictResolutionDeps {
  issueRepo: IssueRepo;
  workflowLogRepo: WorkflowLogRepo;
  sessionStreamLogRepo?: SessionStreamLogRepo;
  coderSessionRepo: CoderSessionRepo;
  eventBus: EventBus;
  opencodeBinPath?: string;
}

export async function resolveConflictsViaAgent(
  deps: ConflictResolutionDeps,
  issueId: string,
  projectId: string,
  worktreePath: string,
  conflictFiles: string[],
): Promise<{ success: boolean; error?: string }> {
  const { issueRepo, workflowLogRepo, sessionStreamLogRepo, coderSessionRepo, eventBus, opencodeBinPath } = deps;

  const issue = issueRepo.findById(issueId);
  if (!issue) {
    return { success: false, error: 'Issue not found for conflict resolution' };
  }

  const acpOptions: AgentSessionOptions = {
    cwd: worktreePath,
    issueId: issue.id,
    projectId,
    workflowLogRepo,
    sessionStreamLogRepo,
    coderSessionRepo,
    eventBus,
    issueNumber: issue.number,
    opencodeBinPath,
  };

  try {
    const prompt = buildConflictResolutionPrompt(issue, worktreePath, conflictFiles, loadAgentConfig(worktreePath));

    const connection = await createAcpConnection(acpOptions);
    try {
      const result = await connection.prompt(prompt);
      if (!result.success) {
        return { success: false, error: result.error || 'Agent ACP session failed' };
      }
      return { success: true };
    } finally {
      await connection.close().catch(() => {});
    }
  } catch (err) {
    return { success: false, error: err instanceof Error ? err.message : String(err) };
  }
}
