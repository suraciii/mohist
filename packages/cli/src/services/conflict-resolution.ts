import { buildConflictResolutionPrompt } from '../agents/artifact-prompt';
import { createAcpConnection, type AcpConnectionOptions } from '../agent-runtime/acp-session';
import type { IssueRepo } from '../db/issue-repo';
import type { WorkflowLogRepo } from '../db/workflow-log-repo';
import type { CoderSessionRepo } from '../db/coder-session-repo';
import type { EventBus } from './event-bus';

export interface ConflictResolutionDeps {
  issueRepo: IssueRepo;
  workflowLogRepo: WorkflowLogRepo;
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
  const { issueRepo, workflowLogRepo, coderSessionRepo, eventBus, opencodeBinPath } = deps;

  const issue = issueRepo.findById(issueId);
  if (!issue) {
    return { success: false, error: 'Issue not found for conflict resolution' };
  }

  const acpOptions: AcpConnectionOptions = {
    cwd: worktreePath,
    issueId: issue.id,
    projectId,
    workflowLogRepo,
    coderSessionRepo,
    eventBus,
    issueNumber: issue.number,
    opencodeBinPath,
  };

  try {
    const prompt = buildConflictResolutionPrompt(issue, worktreePath, conflictFiles);

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
