import { buildConflictResolutionPrompt } from '../agents/artifact-prompt';
import { AgentSession, type AgentSessionOptions } from '../agent-runtime/agent-session';
import type { IssueRepo } from '../db/issue-repo';
import type { WorkflowLogRepo } from '../db/workflow-log-repo';
import type { SessionStreamLogRepo } from '../db/session-stream-log-repo';
import type { CoderSessionRepo } from '../db/coder-session-repo';
import type { EventBus } from './event-bus';
import { loadAgentConfig } from '../agents/agent-config';
import { load as loadConfig } from '../config/config-loader';
import { resolveStageModel } from '../config/model-resolution';
import { Stage } from '../types';
import { createWorkflowSessionObservers } from '../agent-runtime';
import { findChangeDir } from '../openspec/detector';

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

  const config = loadConfig();

  const wfObservers = createWorkflowSessionObservers({
    eventBus,
    workflowLogRepo,
    sessionStreamLogRepo,
    coderSessionRepo,
    stage: 'conflict-resolution',
    title: 'Conflict Resolution',
  });

  const acpOptions: AgentSessionOptions = {
    cwd: worktreePath,
    issueId: issue.id,
    projectId,
    issueNumber: issue.number,
    opencodeBinPath,
    model: resolveStageModel(Stage.Build, config, issue),
    observers: wfObservers,
  };

  try {
    const changeDir = findChangeDir(worktreePath, issue.number);
    const prompt = buildConflictResolutionPrompt(issue, worktreePath, conflictFiles, loadAgentConfig(worktreePath), changeDir);

    const session = await AgentSession.create(acpOptions);
    try {
      const result = await session.execute(prompt, { kind: 'recovery' });
      if (!result.success) {
        return { success: false, error: result.error || 'Agent ACP session failed' };
      }
      return { success: true };
    } finally {
      await session.close().catch(() => {});
    }
  } catch (err) {
    return { success: false, error: err instanceof Error ? err.message : String(err) };
  }
}
