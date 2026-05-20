import * as fs from 'fs';
import * as path from 'path';
import type { CheckResult, StageContext, StageTaskResult } from '../stage-context';
import { emitStageTaskUpdate } from '../stage-context';
import { AgentSession, type AgentSessionOptions } from '../../agent-runtime/agent-session';
import { createWorkflowSessionObservers } from '../../agent-runtime';
import { formatAgentPrompt } from '../../agents/agent-prompt-schema';
import { formatIssueInfo, listOpenSpecContextFiles } from '../../agents/workflow-context';
import { loadAgentConfig } from '../../agents/agent-config';
import { Log } from '../../util/log';

const log = Log.create({ service: 'plan-repair-task' });

export interface PlanRepairTaskOptions {
  worktreePath: string;
  failedCheck: CheckResult;
  attempt: number;
}

const PLAN_ARTIFACT_FILES = [
  'proposal.md',
  'specs',
  'design.md',
  'tasks.json',
  'self-review.md',
];

function detectMissingArtifacts(changeDir: string): string[] {
  const missing: string[] = [];
  for (const artifact of PLAN_ARTIFACT_FILES) {
    const fullPath = path.join(changeDir, artifact);
    if (!fs.existsSync(fullPath)) {
      missing.push(artifact);
    } else if (artifact.endsWith('.md') || artifact.endsWith('.json')) {
      const stat = fs.statSync(fullPath);
      if (stat.size === 0) {
        missing.push(`${artifact} (empty)`);
      }
    }
  }
  return missing;
}

function detectCreatedOrUpdatedArtifacts(changeDir: string, beforeMtimes: Map<string, number>): string[] {
  const updated: string[] = [];
  for (const artifact of PLAN_ARTIFACT_FILES) {
    const fullPath = path.join(changeDir, artifact);
    if (!fs.existsSync(fullPath)) continue;
    const before = beforeMtimes.get(artifact);
    const stat = fs.statSync(fullPath);
    if (before === undefined || stat.mtimeMs > before) {
      updated.push(artifact);
    }
  }
  return updated;
}

function captureMtimes(changeDir: string): Map<string, number> {
  const mtimes = new Map<string, number>();
  for (const artifact of PLAN_ARTIFACT_FILES) {
    const fullPath = path.join(changeDir, artifact);
    if (fs.existsSync(fullPath)) {
      mtimes.set(artifact, fs.statSync(fullPath).mtimeMs);
    }
  }
  return mtimes;
}

function buildRepairPrompt(ctx: StageContext, options: PlanRepairTaskOptions, changeDir: string): string {
  const missing = detectMissingArtifacts(changeDir);

  const parts = [
    `Change Directory: ${changeDir}`,
    '',
    formatIssueInfo(ctx.issue),
    '',
    `Failed check: ${options.failedCheck.name}`,
    '',
    'Failure Summary:',
    options.failedCheck.message ?? 'Plan artifact check failed.',
    '',
  ];

  if (missing.length > 0) {
    parts.push(
      `Missing or Invalid Artifacts:`,
      '',
      ...missing.map(m => `- ${m}`),
      '',
    );
  }

  parts.push(
    `Expected Durable Artifacts:`,
    'The following files should exist and be non-empty in the change directory:',
    '',
    ...PLAN_ARTIFACT_FILES.map(a => `- ${a}`),
  );

  return formatAgentPrompt({
    role: 'Repair invalid Plan artifacts for this issue',
    projectContext: loadAgentConfig(options.worktreePath).context,
    contextFiles: listOpenSpecContextFiles(changeDir, { includeReports: true, includeSessionMemories: true }),
    task: parts.join('\n'),
    contract: `Create or update only the missing or invalid artifact files under ${changeDir}. Do not modify artifacts that already pass their checks.`,
    instruction: [
      '1. Read the issue and every @file context reference before editing.',
      '2. Identify which plan artifact(s) are missing or invalid based on the failed check.',
      '3. Create or update only the missing or invalid artifact(s).',
      `4. Each artifact must be written to the correct path under: ${changeDir}`,
      '5. Follow the existing artifact format and content conventions.',
    ].join('\n'),
  });
}

export async function runPlanRepairTask(
  ctx: StageContext,
  options: PlanRepairTaskOptions,
): Promise<StageTaskResult> {
  const startedAt = Date.now();
  const taskId = 'repair-plan-artifacts';
  const title = 'Repair plan artifacts';
  const stage = 'plan';
  const attempt = options.attempt;

  const changeDir = ctx.artifactManager.getChangeDir(ctx.issue.number);
  if (!changeDir) {
    return {
      taskId,
      title,
      status: 'failed',
      artifacts: [],
      attempts: attempt,
      duration: Date.now() - startedAt,
      output: {
        kind: 'plan-repair-task',
        checkName: options.failedCheck.name,
        attempt,
        success: false,
        error: 'No change directory found',
      },
    };
  }

  const beforeMtimes = captureMtimes(changeDir);

  emitStageTaskUpdate(
    ctx.eventBus,
    ctx.issue.id,
    ctx.issue.projectId,
    stage,
    taskId,
    title,
    'started',
    attempt,
    [],
  );

  const observers = createWorkflowSessionObservers({
    eventBus: ctx.eventBus,
    workflowLogRepo: ctx.workflowLogRepo,
    sessionStreamLogRepo: ctx.sessionStreamLogRepo,
    coderSessionRepo: ctx.coderSessionRepo,
    stage,
    title,
  });

  const acpOptions: AgentSessionOptions = {
    ...ctx.acpOptions,
    cwd: options.worktreePath,
    issueId: ctx.issue.id,
    projectId: ctx.issue.projectId,
    issueNumber: ctx.issue.number,
    executionId: `plan-${ctx.issue.number}-${taskId}-${attempt}`,
    stage,
    title,
    observers,
  };

  let session: AgentSession | undefined;
  try {
    session = await AgentSession.create(acpOptions);
    const result = await session.execute(buildRepairPrompt(ctx, options, changeDir), {
      kind: 'recovery',
      title,
    });
    const duration = Date.now() - startedAt;

    const updatedArtifacts = detectCreatedOrUpdatedArtifacts(changeDir, beforeMtimes);

    emitStageTaskUpdate(
      ctx.eventBus,
      ctx.issue.id,
      ctx.issue.projectId,
      stage,
      taskId,
      title,
      result.success ? 'completed' : 'failed',
      attempt,
      updatedArtifacts,
    );

    return {
      taskId,
      title,
      status: result.success ? 'completed' : 'failed',
      artifacts: updatedArtifacts,
      attempts: attempt,
      duration,
      reason: `${title} triggered by failed check: ${options.failedCheck.name}`,
      causedBy: {
        type: 'check-failure',
        checkName: options.failedCheck.name,
        message: options.failedCheck.message,
      },
      output: {
        kind: 'plan-repair-task',
        checkName: options.failedCheck.name,
        attempt,
        success: result.success,
        error: result.error,
        acpSessionId: result.acpSessionId,
        repairedArtifacts: updatedArtifacts,
        summary: result.success
          ? `${title} completed; re-running ${options.failedCheck.name}`
          : `${title} failed: ${result.error ?? 'unknown error'}`,
      },
    };
  } catch (err) {
    const duration = Date.now() - startedAt;
    const error = err instanceof Error ? err.message : String(err);
    log.warn('Plan repair task failed', {
      issueNumber: ctx.issue.number,
      taskId,
      error,
    });
    emitStageTaskUpdate(
      ctx.eventBus,
      ctx.issue.id,
      ctx.issue.projectId,
      stage,
      taskId,
      title,
      'failed',
      attempt,
      [],
    );
    return {
      taskId,
      title,
      status: 'failed',
      artifacts: [],
      attempts: attempt,
      duration,
      reason: `${title} triggered by failed check: ${options.failedCheck.name}`,
      causedBy: {
        type: 'check-failure',
        checkName: options.failedCheck.name,
        message: error,
      },
      output: {
        kind: 'plan-repair-task',
        checkName: options.failedCheck.name,
        attempt,
        success: false,
        error,
      },
    };
  } finally {
    if (session) {
      await session.close().catch(() => {});
    }
  }
}
