import * as fs from 'fs';
import * as path from 'path';
import { DatabaseManager } from '../db/database';
import { findChangeDir } from '../openspec/detector';
import { Stage } from '../types';
import { DEFAULT_STAGE_DEFINITIONS, workflowDefinitionSnapshotFromUnknown } from '../workflow/domain';
import type { CheckState, CheckSuiteChecks } from '../types';
import type { TasksFile } from '../artifacts/change-artifacts-manager';
import type { CheckResult, StageTaskResult } from '../workflow/stage-context';
import type { WorkflowRunWithStageRuns } from '../db/workflow-run-repo';
import type { WorkflowConvergenceState } from '../types';
import { extractReactionOutput } from '../workflow/convergence';
import type { CompiledStageDefinition } from '../workflow/domain';
import { getWorkflowUseDefinition, inferWorkflowCheckUse, inferWorkflowTaskUse, unwrapWorkflowUseOutput } from '../workflow/uses-catalog';

export type StageTaskStatus = 'pending' | 'running' | 'completed' | 'failed' | 'skipped';
export type StageCheckStatus = 'pending' | 'running' | 'passed' | 'failed' | 'error';
export type StageStateStatus = 'pending' | 'running' | 'awaiting-approval' | 'passed' | 'failed' | 'skipped';

export interface StageTaskCause {
  type: 'check-failure' | 'health-gate-failure' | 'retry' | 'rebase' | 'merge-conflict' | 'unknown';
  checkName?: string;
  taskId?: string;
  message?: string;
}

export interface StageTaskState {
  taskId: string;
  title: string;
  status: StageTaskStatus;
  source: 'static' | 'dynamic';
  order: number;
  attempts: number;
  duration: number;
  artifacts: string[];
  output: unknown;
  startedAt: string | null;
  completedAt: string | null;
  updatedAt: string;
  reason?: string;
  causedBy?: StageTaskCause;
}

export interface StageCheckState {
  checkName: string;
  status: StageCheckStatus;
  message: string | null;
  output: unknown;
  runCount: number;
  lastRunAt: string | null;
  updatedAt: string;
}

export interface StageApprovalState {
  status: string;
  output: unknown;
  requestedAt: string | null;
  respondedAt: string | null;
  staleEvidenceDetected?: boolean;
}

export interface StageFailureDetails {
  reason: string;
  stage: Stage;
  taskId?: string;
  checkName?: string;
  message?: string | null;
  causedBy?: StageTaskCause | null;
}

export type CheckRepairStatus = 'not-needed' | 'available' | 'pending' | 'running' | 'completed' | 'exhausted';

export interface CheckRepairState {
  checkName: string;
  fixTaskId: string;
  status: CheckRepairStatus;
  attemptsUsed: number;
  attemptsMax: number;
  attemptsRemaining: number;
  repairAvailable: boolean;
  lastRepairTask: StageTaskState | null;
  lastRepairStatus: StageTaskStatus | null;
  followUpReviewStatus: StageCheckStatus | null;
  stopReason: string | null;
  unresolvedSummary: string | null;
}

export interface StageDeliveryMetadata {
  specSync: { status: StageTaskStatus; output: unknown } | null;
  archive: { status: StageTaskStatus; output: unknown } | null;
  merge: {
    status: StageTaskStatus;
    output: unknown;
    targetBranch: string | null;
    baseSha: string | null;
    candidateHeadSha: string | null;
    landedSha: string | null;
    rebased: boolean | null;
  } | null;
  remotePr?: {
    status: StageTaskStatus;
    output: unknown;
    prUrl: string | null;
    base: string | null;
    branch: string | null;
    headSha: string | null;
  } | null;
  remoteMerge?: {
    status: StageTaskStatus | StageCheckStatus;
    output: unknown;
    mergedSha: string | null;
  } | null;
  health: { status: StageCheckStatus; message: string | null; output: unknown } | null;
  frozen: boolean;
}

export interface StageStateRead {
  stage: Stage;
  status: StageStateStatus;
  tasks: StageTaskState[];
  checks: StageCheckState[];
  approval: StageApprovalState | null;
  attempts: number;
  startedAt: string | null;
  completedAt: string | null;
  updatedAt: string;
  failure?: StageFailureDetails | null;
  deliveryMetadata?: StageDeliveryMetadata | null;
  checkRepair?: CheckRepairState;
  convergence?: WorkflowConvergenceState;
}

interface StageTaskRow {
  issue_id: string;
  stage: string;
  task_id: string;
  title: string;
  status: string;
  source: string;
  task_order: number;
  attempts: number;
  duration: number;
  artifacts: string;
  output: string | null;
  started_at: string | null;
  completed_at: string | null;
  updated_at: string;
}

interface StageCheckRow {
  issue_id: string;
  stage: string;
  check_name: string;
  status: string;
  message: string | null;
  output: string | null;
  run_count: number;
  last_run_at: string | null;
  updated_at: string;
}

interface StageStateRow {
  issue_id: string;
  stage: string;
  status: string;
  started_at: string | null;
  completed_at: string | null;
  approval_status: string | null;
  approval_output: string | null;
  approval_requested_at: string | null;
  approval_responded_at: string | null;
  attempts: number;
  updated_at: string;
}

interface IssueLookupRow {
  id: string;
  number: number;
  project_id: string;
  stage: string;
  approval_state: string | null;
}

interface ProjectLookupRow {
  id: string;
  path: string;
}

interface StageExecutionProjectionRow {
  issue_id: string;
  stage: string;
  status: string;
  task_results: string;
  check_results: string;
  created_at: string;
  updated_at: string;
}

interface CheckSuiteProjectionRow {
  issue_id: string;
  status: string;
  checks: string;
  updated_at: string;
}

interface LegacyApprovalState {
  stage?: Stage;
  status: string;
  output?: unknown;
  requestedAt?: string;
  respondedAt?: string;
}

interface ProjectedStageSeed {
  status: StageStateStatus;
  startedAt: string | null;
  completedAt: string | null;
  attempts: number;
  updatedAt: string;
}

function rowToStageTask(row: StageTaskRow): StageTaskState {
  const base = {
    taskId: row.task_id,
    title: row.title,
    status: row.status as StageTaskStatus,
    source: row.source as 'static' | 'dynamic',
    order: row.task_order,
    attempts: row.attempts,
    duration: row.duration,
    artifacts: JSON.parse(row.artifacts || '[]'),
    output: row.output ? JSON.parse(row.output) : null,
    startedAt: row.started_at,
    completedAt: row.completed_at,
    updatedAt: row.updated_at,
  };

  const explanation = RUNTIME_TASK_EXPLANATIONS[row.task_id];
  if (explanation) {
    return {
      ...base,
      title: explanation.title || base.title,
      reason: explanation.reason,
      causedBy: {
        type: explanation.causedByType,
        checkName: explanation.causedByCheckName,
      },
    };
  }

  return base;
}

function rowToStageCheck(row: StageCheckRow): StageCheckState {
  return {
    checkName: row.check_name,
    status: row.status as StageCheckStatus,
    message: row.message,
    output: row.output ? JSON.parse(row.output) : null,
    runCount: row.run_count,
    lastRunAt: row.last_run_at,
    updatedAt: row.updated_at,
  };
}

function workflowTaskId(task: { taskId?: string; id?: string }): string {
  return task.taskId ?? task.id ?? 'unknown-task';
}

function workflowTaskOrder(task: { taskOrder?: number; order?: number }): number {
  return task.taskOrder ?? task.order ?? 0;
}

function workflowCheckName(check: { checkName?: string; name?: string }): string {
  return check.checkName ?? check.name ?? 'unknown-check';
}

function workflowDefinitionSnapshotFromRun(run: WorkflowRunWithStageRuns): ReturnType<typeof workflowDefinitionSnapshotFromUnknown> {
  const persisted = workflowDefinitionSnapshotFromUnknown(run.workflowDefinition);
  if (persisted) return persisted;
  if ('snapshot' in run && typeof run.snapshot === 'function') {
    return workflowDefinitionSnapshotFromUnknown(run.snapshot().workflowDefinitionSnapshot);
  }
  return null;
}

function rowToStageStateRead(
  row: StageStateRow,
  tasks: StageTaskState[],
  checks: StageCheckState[],
): StageStateRead {
  let approval: StageApprovalState | null = null;
  if (row.approval_status) {
    approval = {
      status: row.approval_status,
      output: row.approval_output ? JSON.parse(row.approval_output) : null,
      requestedAt: row.approval_requested_at,
      respondedAt: row.approval_responded_at,
    };
  }

  return {
    stage: row.stage as Stage,
    status: row.status as StageStateStatus,
    tasks,
    checks,
    approval,
    attempts: row.attempts,
    startedAt: row.started_at,
    completedAt: row.completed_at,
    updatedAt: row.updated_at,
  };
}

function normalizeStageStateStatus(raw: string): StageStateStatus {
  switch (raw) {
    case 'running':
      return 'running';
    case 'awaiting-approval':
      return 'awaiting-approval';
    case 'passed':
      return 'passed';
    case 'failed':
      return 'failed';
    case 'skipped':
      return 'skipped';
    default:
      return 'pending';
  }
}

export interface UpsertTaskInput {
  taskId: string;
  title: string;
  status: StageTaskStatus;
  source?: 'static' | 'dynamic';
  order?: number;
  attempts?: number;
  duration?: number;
  artifacts?: string[];
  output?: unknown;
  startedAt?: string | null;
  completedAt?: string | null;
}

export interface UpsertCheckInput {
  checkName: string;
  status: StageCheckStatus;
  message?: string | null;
  output?: unknown;
  runCount?: number;
  lastRunAt?: string | null;
}

export interface SetApprovalInput {
  status: string;
  output?: unknown;
  requestedAt?: string | null;
  respondedAt?: string | null;
  staleEvidenceDetected?: boolean;
}

export function normalizeCheckStatus(raw: string): StageCheckStatus {
  switch (raw) {
    case 'pass':
    case 'passed':
      return 'passed';
    case 'fail':
    case 'failed':
      return 'failed';
    case 'error':
      return 'error';
    case 'running':
      return 'running';
    default:
      return 'pending';
  }
}

export function normalizeTaskStatus(raw: string): StageTaskStatus {
  switch (raw) {
    case 'completed':
      return 'completed';
    case 'failed':
      return 'failed';
    case 'running':
      return 'running';
    case 'skipped':
      return 'skipped';
    default:
      return 'pending';
  }
}

interface StaticTaskDef {
  taskId: string;
  title: string;
  order: number;
}

const REAL_TASK_IDS: Record<Stage, Set<string>> = {
  [Stage.Plan]: new Set(['proposal', 'specs', 'design', 'tasks', 'self-review', 'repair-plan-artifacts', 'fix-plan-health']),
  [Stage.Build]: new Set(['fix-build-health', 'repair-build']),
  [Stage.Check]: new Set(['ai-review', 'fix-review-findings', 'repair-review-findings', 'repair-merge', 'fix-check-health']),
  [Stage.Integrate]: new Set(['integrate:spec-sync', 'integrate:archive-change', 'integrate:merge', 'merge-branch', 'verify-merge', 'repair-merge', 'rebase-branch', 'fix-integrate-health']),
  [Stage.Done]: new Set([]),
  [Stage.Backlog]: new Set([]),
};

function isRealTask(stage: Stage, taskId: string): boolean {
  const allowed = REAL_TASK_IDS[stage];
  if (!allowed) {
    return taskId.startsWith('repair-') || taskId.startsWith('fix-');
  }
  if (allowed.has(taskId)) {
    return true;
  }
  if (stage === Stage.Build && /^T-\d+$/.test(taskId)) {
    return true;
  }
  return false;
}

interface TaskExplanationDef {
  title: string;
  reason?: string;
  causedByType: StageTaskCause['type'];
  causedByCheckName?: string;
}

const RUNTIME_TASK_EXPLANATIONS: Record<string, TaskExplanationDef> = {
  'repair-plan-artifacts': {
    title: 'Repair plan artifacts',
    reason: 'Added after plan artifact check failed',
    causedByType: 'check-failure',
    causedByCheckName: 'plan-artifact-check',
  },
  'fix-plan-health': {
    title: 'Fix plan health',
    reason: 'Added after plan health gate failed',
    causedByType: 'health-gate-failure',
  },
  'fix-build-health': {
    title: 'Fix build health',
    reason: 'Added after build health gate failed',
    causedByType: 'health-gate-failure',
  },
  'repair-build': {
    title: 'Repair build',
    reason: 'Added after build failed',
    causedByType: 'check-failure',
  },
  'fix-review-findings': {
    title: 'Fix review findings',
    reason: 'Added after review passed failed',
    causedByType: 'check-failure',
    causedByCheckName: 'ai-review',
  },
  'repair-merge': {
    title: 'Repair merge',
    reason: 'Added after merge check failed',
    causedByType: 'check-failure',
  },
  'fix-check-health': {
    title: 'Fix check health',
    reason: 'Added after check health gate failed',
    causedByType: 'health-gate-failure',
  },
  'rebase-branch': {
    title: 'Rebase branch',
    reason: 'Added because target branch moved',
    causedByType: 'rebase',
  },
};

const PLAN_TASK_DEFS: StaticTaskDef[] = [];

const CHECK_TASK_DEFS: StaticTaskDef[] = [];

const INTEGRATE_TASK_DEFS: StaticTaskDef[] = [
  { taskId: 'integrate:spec-sync', title: 'Sync specs', order: 0 },
  { taskId: 'integrate:archive-change', title: 'Archive change', order: 1 },
  { taskId: 'integrate:merge', title: 'Merge branch', order: 2 },
];

const STATIC_TASK_DEFS: Partial<Record<Stage, StaticTaskDef[]>> = {
  [Stage.Plan]: PLAN_TASK_DEFS,
  [Stage.Check]: CHECK_TASK_DEFS,
  [Stage.Integrate]: INTEGRATE_TASK_DEFS,
};

function isTaskAttemptForBase(taskId: string, baseTaskId: string): boolean {
  return taskId === baseTaskId || taskId.startsWith(`${baseTaskId}:`);
}

function isLegacyFixReviewFindingsTask(taskId: string): boolean {
  return isTaskAttemptForBase(taskId, 'fix-review-findings') || isTaskAttemptForBase(taskId, 'repair-review-findings');
}

function stageDefinitionForStage(stage: Stage, stageDefinition?: CompiledStageDefinition): CompiledStageDefinition | undefined {
  return stageDefinition ?? DEFAULT_STAGE_DEFINITIONS.find(definition => definition.stage === stage);
}

function approvalVerdictCheckName(stageDefinition?: CompiledStageDefinition): string {
  return stageDefinition?.approvalEvidencePolicy?.verdictCheckName ?? 'review-passed';
}

function approvalVerdictRepairPolicy(stageDefinition?: CompiledStageDefinition): { fixTaskId: string; maxAttempts: number } {
  const verdictCheckName = approvalVerdictCheckName(stageDefinition);
  const policy = stageDefinition?.repairPolicies?.find(candidate => candidate.checkName === verdictCheckName)
    ?? stageDefinition?.checkFailurePolicies?.find(candidate => candidate.checkName === verdictCheckName);
  return {
    fixTaskId: policy?.fixTaskId ?? 'fix-review-findings',
    maxAttempts: policy?.maxAttempts ?? 0,
  };
}

function isApprovalVerdictFixTask(taskId: string, stageDefinition?: CompiledStageDefinition): boolean {
  const repairPolicy = approvalVerdictRepairPolicy(stageDefinition);
  return isTaskAttemptForBase(taskId, repairPolicy.fixTaskId) || isLegacyFixReviewFindingsTask(taskId);
}

function extractUnresolvedSummary(output: unknown, message: string | null): string | null {
  if (!output || typeof output !== 'object') {
    return message ?? null;
  }
  const obj = output as Record<string, unknown>;
  const source = obj.structuredResult && typeof obj.structuredResult === 'object'
    ? obj.structuredResult as Record<string, unknown>
    : obj;
  if (typeof obj.unresolvedSummary === 'string' && obj.unresolvedSummary.length > 0) {
    return obj.unresolvedSummary;
  }
  if (typeof source.verdict === 'string' && source.verdict === 'FAIL' && typeof source.summary === 'string' && source.summary.length > 0) {
    return source.summary;
  }
  if (typeof obj.message === 'string' && obj.message.length > 0) {
    return obj.message;
  }
  return message ?? null;
}

function computeCheckRepairState(
  tasks: StageTaskState[],
  checks: StageCheckState[],
  stageDefinition?: CompiledStageDefinition,
): CheckRepairState | null {
  const verdictCheckName = approvalVerdictCheckName(stageDefinition);
  const repairPolicy = approvalVerdictRepairPolicy(stageDefinition);
  const fixTasks = tasks.filter(task => isApprovalVerdictFixTask(task.taskId, stageDefinition));

  if (fixTasks.length === 0) {
    const verdictCheck = checks.find(c => c.checkName === verdictCheckName);
    if (!verdictCheck || verdictCheck.status === 'pending' || verdictCheck.status === 'running') {
      return null;
    }
  }

  const verdictCheck = checks.find(c => c.checkName === verdictCheckName);
  const maxAttempts = repairPolicy.maxAttempts;

  const completedFixTasks = fixTasks.filter(t => t.status === 'completed');
  const pendingFixTasks = fixTasks.filter(t => t.status === 'pending' || t.status === 'running');
  const attemptsUsed = fixTasks.length;
  const attemptsRemaining = Math.max(0, maxAttempts - attemptsUsed);

  const repairInProgress = pendingFixTasks.length > 0;
  const repairAvailable = attemptsRemaining > 0 && verdictCheck?.status === 'failed' && !repairInProgress;

  let status: CheckRepairStatus;
  if (verdictCheck?.status === 'passed') {
    status = 'not-needed';
  } else if (repairInProgress) {
    status = pendingFixTasks.some(t => t.status === 'running') ? 'running' : 'pending';
  } else if (completedFixTasks.length > 0) {
    status = verdictCheck?.status === 'failed'
      ? repairAvailable ? 'available' : 'exhausted'
      : 'completed';
  } else {
    status = repairAvailable ? 'available' : 'exhausted';
  }

  const lastRepairTask = fixTasks.at(-1) ?? null;
  const lastRepairStatus: StageTaskStatus | null = lastRepairTask?.status ?? null;

  const followUpReviewStatus: StageCheckStatus | null = verdictCheck?.status ?? null;

  let stopReason: CheckRepairState['stopReason'] = null;
  if (verdictCheck?.status === 'passed') {
    stopReason = verdictCheckName;
  } else if (pendingFixTasks.some(t => t.status === 'pending')) {
    stopReason = 'repair-pending';
  } else if (pendingFixTasks.some(t => t.status === 'running')) {
    stopReason = 'repair-running';
  } else if (attemptsRemaining === 0 && completedFixTasks.length > 0) {
    stopReason = 'max-repair-attempts-reached';
  }

  const unresolvedSummary = verdictCheck?.status === 'failed'
    ? extractUnresolvedSummary(verdictCheck.output, verdictCheck.message)
    : null;

  return {
    checkName: verdictCheckName,
    fixTaskId: repairPolicy.fixTaskId,
    status,
    attemptsUsed,
    attemptsMax: maxAttempts,
    attemptsRemaining,
    repairAvailable,
    lastRepairTask,
    lastRepairStatus,
    followUpReviewStatus,
    stopReason,
    unresolvedSummary,
  };
}

function extractStructuredResult(output: unknown): {
  verdict?: string;
  marker?: string;
  items?: Array<{ id: string; severity: string; status?: string; scope?: string; evidence: string; suggestedAction?: string; verification?: string }>;
  evidence?: string;
  repairedItemIds?: string[];
  summary?: string;
} | null {
  if (!output || typeof output !== 'object') return null;
  const obj = output as Record<string, unknown>;
  const source = obj.structuredResult && typeof obj.structuredResult === 'object'
    ? obj.structuredResult as Record<string, unknown>
    : obj;
  return {
    verdict: typeof source.verdict === 'string' ? source.verdict : undefined,
    marker: typeof source.marker === 'string' ? source.marker : undefined,
    items: Array.isArray(source.items) ? source.items as Array<{ id: string; severity: string; status?: string; scope?: string; evidence: string; suggestedAction?: string; verification?: string }> : undefined,
    evidence: typeof source.evidence === 'string' ? source.evidence : undefined,
    repairedItemIds: Array.isArray(source.repairedItemIds) ? source.repairedItemIds as string[] : undefined,
    summary: typeof source.summary === 'string' ? source.summary : undefined,
  };
}

function computeConvergenceState(
  tasks: StageTaskState[],
  checks: StageCheckState[],
  stageDefinition?: CompiledStageDefinition,
): WorkflowConvergenceState | null {
  const fixTasks = tasks.filter(task => isApprovalVerdictFixTask(task.taskId, stageDefinition));

  const failedCheck = checks.find(c => c.status === 'failed' || c.status === 'error');

  if (!failedCheck && fixTasks.length === 0) {
    return null;
  }

  const blockingItems: string[] = [];
  const nonBlockingItems: string[] = [];
  let directlyRepairedCount = 0;
  const attemptedItemIds: string[] = [];
  const resolvedItemIds: string[] = [];
  const unresolvedItemIds: string[] = [];
  const newBlockingItemIds: string[] = [];

  if (failedCheck) {
    const structured = extractStructuredResult(failedCheck.output);
    if (structured?.items) {
      for (const item of structured.items) {
        if (item.severity === 'blocking') {
          blockingItems.push(item.id);
        } else {
          nonBlockingItems.push(item.id);
        }
      }
    }
  }

  for (const task of tasks) {
    const structured = extractStructuredResult(task.output);
    if (structured?.repairedItemIds) {
      directlyRepairedCount += structured.repairedItemIds.length;
    }
    if (isApprovalVerdictFixTask(task.taskId, stageDefinition) && task.status === 'completed') {
      const reaction = extractReactionOutput({
        taskId: task.taskId,
        title: task.title,
        status: 'completed',
        artifacts: task.artifacts,
        attempts: task.attempts,
        duration: task.duration,
        output: task.output,
      });
      if (reaction) {
        for (const id of reaction.attemptedItemIds) {
          if (!attemptedItemIds.includes(id)) attemptedItemIds.push(id);
        }
        for (const id of reaction.resolvedItemIds) {
          if (!resolvedItemIds.includes(id)) resolvedItemIds.push(id);
        }
        for (const id of reaction.unresolvedItemIds) {
          if (!unresolvedItemIds.includes(id)) unresolvedItemIds.push(id);
        }
        continue;
      }
      if (structured?.items) {
        for (const item of structured.items) {
          if (!attemptedItemIds.includes(item.id)) {
            attemptedItemIds.push(item.id);
          }
          if (item.status === 'resolved') {
            if (!resolvedItemIds.includes(item.id)) {
              resolvedItemIds.push(item.id);
            }
          } else if (item.status === 'unresolved') {
            if (!unresolvedItemIds.includes(item.id)) {
              unresolvedItemIds.push(item.id);
            }
          }
        }
      }
    }
  }

  if (fixTasks.length > 0) {
    for (const id of blockingItems) {
      if (!attemptedItemIds.includes(id) && !resolvedItemIds.includes(id)) {
        newBlockingItemIds.push(id);
      }
    }
  }

  const blockedReason = failedCheck
    ? extractUnresolvedSummary(failedCheck.output, failedCheck.message)
    : (fixTasks.length > 0 ? 'Reaction task completed but check not passed' : null) ?? undefined;

  return {
    failedCheck: failedCheck?.checkName,
    blockingItemCount: blockingItems.length,
    directlyRepairedCount,
    reactionAttempts: fixTasks.length,
    attemptedItemIds,
    resolvedItemIds,
    unresolvedItemIds,
    newBlockingItemIds,
    nonBlockingItemIds: nonBlockingItems,
    blockedReason: blockedReason ?? undefined,
  };
}

export class StageStateService {
  constructor(private db: DatabaseManager) {}

  setStageStatus(issueId: string, stage: Stage, status: StageStateStatus): void {
    const now = new Date().toISOString();
    const completedAt = (status === 'passed' || status === 'failed' || status === 'skipped') ? now : null;

    this.db.run(
      `UPDATE stage_states SET status = ?, completed_at = COALESCE(?, completed_at), updated_at = ?
       WHERE issue_id = ? AND stage = ?`,
      [status, completedAt, now, issueId, stage],
    );
  }

  ensureStage(issueId: string, stage: Stage): void {
    const now = new Date().toISOString();
    this.db.transaction(() => {
      const existing = this.db.get<StageStateRow>(
        'SELECT * FROM stage_states WHERE issue_id = ? AND stage = ?',
        [issueId, stage],
      );

      if (!existing) {
        this.db.run(
          `INSERT INTO stage_states (issue_id, stage, status, attempts, started_at, updated_at)
           VALUES (?, ?, 'running', 0, ?, ?)`,
          [issueId, stage, now, now],
        );
        this.seedStaticTasks(issueId, stage);
      } else {
        this.db.run(
          `UPDATE stage_states SET attempts = attempts + 1, status = 'running', started_at = ?, completed_at = NULL, updated_at = ?
           WHERE issue_id = ? AND stage = ?`,
          [now, now, issueId, stage],
        );
      }
    });
  }

  private seedStaticTasks(issueId: string, stage: Stage): void {
    const defs = STATIC_TASK_DEFS[stage];
    if (!defs) return;

    const now = new Date().toISOString();
    for (const def of defs) {
      this.db.run(
        `INSERT OR IGNORE INTO stage_tasks
         (issue_id, stage, task_id, title, status, source, task_order, attempts, duration, artifacts, output, started_at, completed_at, updated_at)
         VALUES (?, ?, ?, ?, 'pending', 'static', ?, 0, 0, '[]', NULL, NULL, NULL, ?)`,
        [issueId, stage, def.taskId, def.title, def.order, now],
      );
    }
  }

  upsertTask(issueId: string, stage: Stage, input: UpsertTaskInput): void {
    const now = new Date().toISOString();
    const source = input.source ?? 'dynamic';
    const order = input.order ?? 0;
    const attempts = input.attempts ?? 1;
    const duration = input.duration ?? 0;
    const artifacts = JSON.stringify(input.artifacts ?? []);
    const output = input.output != null ? JSON.stringify(input.output) : null;
    const startedAt = input.startedAt ?? (input.status === 'running' ? now : null);
    const completedAt = input.completedAt ?? (input.status === 'completed' || input.status === 'failed' || input.status === 'skipped' ? now : null);

    this.ensureStageRowExists(issueId, stage);

    this.db.run(
      `INSERT INTO stage_tasks
        (issue_id, stage, task_id, title, status, source, task_order, attempts, duration, artifacts, output, started_at, completed_at, updated_at)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
       ON CONFLICT(issue_id, stage, task_id) DO UPDATE SET
         title = excluded.title,
         status = excluded.status,
         source = excluded.source,
         task_order = excluded.task_order,
         attempts = excluded.attempts,
         duration = excluded.duration,
         artifacts = excluded.artifacts,
         output = excluded.output,
         started_at = COALESCE(stage_tasks.started_at, excluded.started_at),
         completed_at = excluded.completed_at,
         updated_at = excluded.updated_at`,
      [issueId, stage, input.taskId, input.title, input.status, source, order, attempts, duration, artifacts, output, startedAt, completedAt, now],
    );
  }

  upsertCheck(issueId: string, stage: Stage, input: UpsertCheckInput): void {
    const now = new Date().toISOString();
    const message = input.message ?? null;
    const output = input.output != null ? JSON.stringify(input.output) : null;
    const runCount = input.runCount;
    const lastRunAt = input.lastRunAt ?? (input.status !== 'pending' ? now : null);

    this.ensureStageRowExists(issueId, stage);

    if (runCount != null) {
      this.db.run(
        `INSERT INTO stage_checks
          (issue_id, stage, check_name, status, message, output, run_count, last_run_at, updated_at)
         VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
         ON CONFLICT(issue_id, stage, check_name) DO UPDATE SET
           status = excluded.status,
           message = excluded.message,
           output = excluded.output,
           run_count = excluded.run_count,
           last_run_at = excluded.last_run_at,
           updated_at = excluded.updated_at`,
        [issueId, stage, input.checkName, input.status, message, output, runCount, lastRunAt, now],
      );
    } else {
      this.db.run(
        `INSERT INTO stage_checks
          (issue_id, stage, check_name, status, message, output, run_count, last_run_at, updated_at)
         VALUES (?, ?, ?, ?, ?, ?, 1, ?, ?)
         ON CONFLICT(issue_id, stage, check_name) DO UPDATE SET
           status = excluded.status,
           message = excluded.message,
           output = excluded.output,
           run_count = stage_checks.run_count + 1,
           last_run_at = excluded.last_run_at,
           updated_at = excluded.updated_at`,
        [issueId, stage, input.checkName, input.status, message, output, lastRunAt, now],
      );
    }
  }

  setApproval(issueId: string, stage: Stage, input: SetApprovalInput): void {
    const now = new Date().toISOString();
    const approvalOutput = input.output != null ? JSON.stringify(input.output) : null;
    const requestedAt = input.requestedAt ?? null;
    const respondedAt = input.respondedAt ?? null;

    this.ensureStageRowExists(issueId, stage);

    this.db.run(
      `UPDATE stage_states SET
         approval_status = ?,
         approval_output = ?,
         approval_requested_at = ?,
         approval_responded_at = ?,
         updated_at = ?
       WHERE issue_id = ? AND stage = ?`,
      [input.status, approvalOutput, requestedAt, respondedAt, now, issueId, stage],
    );
  }

  clearApproval(issueId: string, stage: Stage): void {
    const now = new Date().toISOString();
    this.ensureStageRowExists(issueId, stage);
    this.db.run(
      `UPDATE stage_states SET
         approval_status = NULL,
         approval_output = NULL,
         approval_requested_at = NULL,
         approval_responded_at = NULL,
         updated_at = ?
       WHERE issue_id = ? AND stage = ?`,
      [now, issueId, stage],
    );
  }

  getStageState(issueId: string, stage: Stage): StageStateRead | null {
    const stateRow = this.db.get<StageStateRow>(
      'SELECT * FROM stage_states WHERE issue_id = ? AND stage = ?',
      [issueId, stage],
    );
    if (!stateRow) return null;

    const taskRows = this.db.all<StageTaskRow>(
      'SELECT * FROM stage_tasks WHERE issue_id = ? AND stage = ? ORDER BY task_order ASC, task_id ASC',
      [issueId, stage],
    );
    const checkRows = this.db.all<StageCheckRow>(
      'SELECT * FROM stage_checks WHERE issue_id = ? AND stage = ? ORDER BY check_name ASC',
      [issueId, stage],
    );

    const allTasks = taskRows.map(rowToStageTask);
    const filteredTasks = allTasks.filter(t => isRealTask(stage, t.taskId));
    const stageState = rowToStageStateRead(
      stateRow,
      filteredTasks,
      checkRows.map(rowToStageCheck),
    );

    const stageDefinition = stageDefinitionForStage(stage);
    if (stageDefinition?.approvalEvidencePolicy) {
      const convergence = computeConvergenceState(filteredTasks, stageState.checks, stageDefinition);
      if (convergence) {
        stageState.convergence = convergence;
      }
    }

    return stageState;
  }

  getIssueStageState(issueId: string): StageStateRead[] {
    this.projectLegacyStageState(issueId);

    const stateRows = this.db.all<StageStateRow>(
      'SELECT * FROM stage_states WHERE issue_id = ? ORDER BY stage ASC',
      [issueId],
    );

    return stateRows.map(row => {
      const taskRows = this.db.all<StageTaskRow>(
        'SELECT * FROM stage_tasks WHERE issue_id = ? AND stage = ? ORDER BY task_order ASC, task_id ASC',
        [issueId, row.stage],
      );
      const checkRows = this.db.all<StageCheckRow>(
        'SELECT * FROM stage_checks WHERE issue_id = ? AND stage = ? ORDER BY check_name ASC',
        [issueId, row.stage],
      );
      const allTasks = taskRows.map(rowToStageTask);
      const filteredTasks = allTasks.filter(t => isRealTask(row.stage as Stage, t.taskId));
      const stageState = rowToStageStateRead(
        row,
        filteredTasks,
        checkRows.map(rowToStageCheck),
      );

      const stage = row.stage as Stage;
      const stageDefinition = stageDefinitionForStage(stage);
      if (stageDefinition?.approvalEvidencePolicy) {
        const repair = computeCheckRepairState(filteredTasks, stageState.checks, stageDefinition);
        stageState.checkRepair = repair === null ? undefined : repair;
        const convergence = computeConvergenceState(filteredTasks, stageState.checks, stageDefinition);
        stageState.convergence = convergence === null ? undefined : convergence;
      }

      return stageState;
    });
  }

  getIssueStageStateFromWorkflowRun(run: WorkflowRunWithStageRuns): StageStateRead[] {
    const workflowDefinitionSnapshot = workflowDefinitionSnapshotFromRun(run);
    return run.stageRuns.map(stageRun => {
      const stageDefinition = workflowDefinitionSnapshot?.compiledStageDefinitions.find(definition => definition.stage === stageRun.stage);
      const tasks: StageTaskState[] = stageRun.tasks.map(task => ({
        taskId: workflowTaskId(task),
        title: task.title,
        status: task.status as StageTaskStatus,
        source: 'static' as const,
        order: workflowTaskOrder(task),
        attempts: task.attempts,
        duration: task.duration,
        artifacts: task.artifacts,
        output: task.output,
        startedAt: task.startedAt,
        completedAt: task.completedAt,
        updatedAt: task.updatedAt,
        reason: task.reason ?? undefined,
        causedBy: task.causedByType ? {
          type: task.causedByType as StageTaskCause['type'],
          checkName: task.causedByCheckName ?? undefined,
          taskId: task.causedByTaskId ?? undefined,
          message: task.reason ?? undefined,
        } : undefined,
      }));

      const checks: StageCheckState[] = stageRun.checks.map(check => ({
        checkName: workflowCheckName(check),
        status: check.status as StageCheckStatus,
        message: check.message,
        output: check.output,
        runCount: check.runCount,
        lastRunAt: check.lastRunAt,
        updatedAt: check.updatedAt,
      }));

      let approval: StageApprovalState | null = null;
      if (stageRun.approvalStatus) {
        approval = {
          status: stageRun.approvalStatus,
          output: stageRun.approvalOutput,
          requestedAt: stageRun.approvalRequestedAt,
          respondedAt: stageRun.approvalRespondedAt,
          staleEvidenceDetected: stageRun.staleEvidenceDetected ?? false,
        };
      }

      const status = stageRun.status as StageStateStatus;

      const checkRepairRaw = stageDefinition?.approvalEvidencePolicy
        ? computeCheckRepairState(tasks, checks, stageDefinition)
        : null;

      const convergenceRaw = stageDefinition?.approvalEvidencePolicy
        ? computeConvergenceState(tasks, checks, stageDefinition)
        : null;

      return {
        stage: stageRun.stage,
        status,
        tasks,
        checks,
        approval,
        attempts: 0,
        startedAt: stageRun.startedAt,
        completedAt: stageRun.completedAt,
        updatedAt: stageRun.updatedAt,
        failure: this.workflowRunFailureDetails(stageRun, tasks, checks, stageDefinition),
        deliveryMetadata: this.workflowRunDeliveryMetadata(tasks, checks, stageDefinition),
        ...(checkRepairRaw !== null ? { checkRepair: checkRepairRaw } : {}),
        ...(convergenceRaw !== null ? { convergence: convergenceRaw } : {}),
      };
    });
  }

  private workflowRunDeliveryMetadata(
    tasks: StageTaskState[],
    checks: StageCheckState[],
    stageDefinition?: CompiledStageDefinition,
  ): StageDeliveryMetadata | null {
    const deliveryTasks = tasks
      .map(task => ({ task, use: this.workflowTaskUse(stageDefinition, task.taskId) }))
      .filter(({ use }) => getWorkflowUseDefinition(use)?.deliveryRole !== 'none');
    const deliveryChecks = checks
      .map(check => ({ check, use: this.workflowCheckUse(stageDefinition, check.checkName) }))
      .filter(({ use }) => getWorkflowUseDefinition(use)?.deliveryRole !== 'none');
    const specSync = deliveryTasks.find(({ use }) => getWorkflowUseDefinition(use)?.deliveryRole === 'spec-sync')?.task ?? null;
    const archive = deliveryTasks.find(({ use }) => getWorkflowUseDefinition(use)?.deliveryRole === 'archive')?.task ?? null;
    const merge = deliveryTasks.find(({ use }) => getWorkflowUseDefinition(use)?.deliveryRole === 'local-merge')?.task ?? null;
    const remotePr = deliveryTasks.find(({ use }) => getWorkflowUseDefinition(use)?.deliveryRole === 'remote-pr')?.task ?? null;
    const remoteMerge =
      deliveryTasks.find(({ use }) => getWorkflowUseDefinition(use)?.deliveryRole === 'remote-merge')?.task ??
      deliveryChecks.find(({ use }) => getWorkflowUseDefinition(use)?.deliveryRole === 'remote-merge')?.check ??
      null;
    const health = (specSync || archive || merge || remotePr || remoteMerge)
      ? checks
        .map(check => ({ check, use: this.workflowCheckUse(stageDefinition, check.checkName) }))
        .find(({ use }) => use === 'mohist/health-gate')?.check ?? null
      : null;
    const mergeOutput = unwrapWorkflowUseOutput(merge?.output) ?? {};
    const remotePrOutput = unwrapWorkflowUseOutput(remotePr?.output) ?? {};
    const remoteMergeOutput = unwrapWorkflowUseOutput(remoteMerge?.output) ?? {};

    if (!specSync && !archive && !merge && !remotePr && !remoteMerge && !health) return null;
    return {
      specSync: specSync ? { status: specSync.status, output: specSync.output } : null,
      archive: archive ? { status: archive.status, output: archive.output } : null,
      merge: merge ? {
        status: merge.status,
        output: merge.output,
        targetBranch: typeof mergeOutput.targetBranch === 'string' ? mergeOutput.targetBranch : null,
        baseSha: typeof mergeOutput.baseSha === 'string' ? mergeOutput.baseSha : null,
        candidateHeadSha: typeof mergeOutput.candidateHeadSha === 'string' ? mergeOutput.candidateHeadSha : null,
        landedSha: typeof mergeOutput.landedSha === 'string' ? mergeOutput.landedSha : null,
        rebased: typeof mergeOutput.rebased === 'boolean' ? mergeOutput.rebased : null,
      } : null,
      remotePr: remotePr ? {
        status: remotePr.status,
        output: remotePr.output,
        prUrl: typeof remotePrOutput.prUrl === 'string' ? remotePrOutput.prUrl : null,
        base: typeof remotePrOutput.base === 'string' ? remotePrOutput.base : null,
        branch: typeof remotePrOutput.branch === 'string' ? remotePrOutput.branch : null,
        headSha: typeof remotePrOutput.headSha === 'string' ? remotePrOutput.headSha : null,
      } : null,
      remoteMerge: remoteMerge ? {
        status: remoteMerge.status,
        output: remoteMerge.output,
        mergedSha: typeof remoteMergeOutput.mergedSha === 'string' ? remoteMergeOutput.mergedSha : null,
      } : null,
      health: health ? { status: health.status, message: health.message, output: health.output } : null,
      frozen: this.hasCompletedLockingUse(tasks, checks, stageDefinition),
    };
  }

  private workflowTaskUse(stageDefinition: CompiledStageDefinition | undefined, taskId: string): string {
    const taskDefinition = stageDefinition?.tasks.find(candidate => candidate.id === taskId);
    const policy = stageDefinition?.taskExecutionPolicies?.find(candidate => candidate.taskId === taskId)
      ?? stageDefinition?.taskExecutionPolicies?.find(candidate => candidate.taskId === '*');
    return taskDefinition?.uses ?? inferWorkflowTaskUse(taskId, policy?.kind);
  }

  private workflowCheckUse(stageDefinition: CompiledStageDefinition | undefined, checkName: string): string {
    const checkDefinition = stageDefinition?.checks.find(candidate => candidate.name === checkName);
    return checkDefinition?.uses ?? inferWorkflowCheckUse(checkName);
  }

  private hasCompletedLockingUse(
    tasks: StageTaskState[],
    checks: StageCheckState[],
    stageDefinition?: CompiledStageDefinition,
  ): boolean {
    return tasks.some(task => {
      if (task.status !== 'completed') return false;
      return getWorkflowUseDefinition(this.workflowTaskUse(stageDefinition, task.taskId))?.locksCode === true;
    }) || checks.some(check => {
      if (check.status !== 'passed') return false;
      return getWorkflowUseDefinition(this.workflowCheckUse(stageDefinition, check.checkName))?.locksCode === true;
    });
  }

  private workflowRunFailureDetails(
    stageRun: WorkflowRunWithStageRuns['stageRuns'][number],
    tasks: StageTaskState[],
    checks: StageCheckState[],
    stageDefinition?: CompiledStageDefinition,
  ): StageFailureDetails | null {
    if (stageRun.status !== 'failed') return null;
    const failedTask = tasks.find(task => task.status === 'failed');
    if (failedTask) {
      return {
        reason: 'task-failed',
        stage: stageRun.stage,
        taskId: failedTask.taskId,
        message: failedTask.reason ?? null,
        causedBy: failedTask.causedBy ?? null,
      };
    }
    const failedCheck = checks.find(check => check.status === 'failed' || check.status === 'error');
    if (failedCheck) {
      const codeLocked = this.hasCompletedLockingUse(tasks, checks, stageDefinition);
      return {
        reason: codeLocked ? 'post-delivery-check-failed' : 'check-unrepaired',
        stage: stageRun.stage,
        checkName: failedCheck.checkName,
        message: failedCheck.message,
      };
    }
    if (stageRun.approvalStatus === 'rejected') {
      return { reason: 'approval-rejected', stage: stageRun.stage, message: null };
    }
    return null;
  }

  private ensureStageRowExists(issueId: string, stage: Stage): void {
    const now = new Date().toISOString();
    this.db.run(
      `INSERT OR IGNORE INTO stage_states (issue_id, stage, status, attempts, updated_at)
       VALUES (?, ?, 'pending', 0, ?)`,
      [issueId, stage, now],
    );
  }

  private projectLegacyStageState(issueId: string): void {
    const issue = this.db.get<IssueLookupRow>(
      'SELECT id, number, project_id, stage, approval_state FROM issues WHERE id = ?',
      [issueId],
    );
    if (!issue) return;

    const stageRows = this.db.all<{ stage: string }>(
      'SELECT stage FROM stage_states WHERE issue_id = ?',
      [issueId],
    );
    const persistedStages = new Set(stageRows.map(row => row.stage as Stage));

    const executionRows = this.db.all<StageExecutionProjectionRow>(
      `SELECT issue_id, stage, status, task_results, check_results, created_at, updated_at
       FROM stage_executions
       WHERE issue_id = ?
       ORDER BY created_at ASC`,
      [issueId],
    );
    const suiteRow = this.db.get<CheckSuiteProjectionRow>(
      `SELECT issue_id, status, checks, updated_at
       FROM check_suites
       WHERE issue_id = ? AND status IN ('running', 'awaiting-approval')
       ORDER BY created_at DESC LIMIT 1`,
      [issueId],
    );
    const tasksFile = this.readLegacyTasksFile(issue.number, issue.project_id);

    const hasLegacyEvidence = executionRows.length > 0 || suiteRow != null || tasksFile != null;
    if (!hasLegacyEvidence) return;

    const stagesToSeed = new Set<Stage>();
    for (const row of executionRows) {
      stagesToSeed.add(row.stage as Stage);
    }
    if (suiteRow) stagesToSeed.add(Stage.Check);
    if (tasksFile?.tasks.length) {
      stagesToSeed.add(Stage.Build);
    }

    const approvalState = this.parseJson<LegacyApprovalState | null>(issue.approval_state, null);
    if (approvalState?.stage) {
      stagesToSeed.add(approvalState.stage);
    }

    for (const stage of stagesToSeed) {
      if (persistedStages.has(stage)) continue;
      this.seedProjectedStageState(issueId, stage, executionRows, suiteRow, tasksFile, approvalState, issue.stage as Stage);
    }
  }

  private seedProjectedStageState(
    issueId: string,
    stage: Stage,
    executionRows: StageExecutionProjectionRow[],
    suiteRow: CheckSuiteProjectionRow | undefined,
    tasksFile: TasksFile | null,
    approvalState: LegacyApprovalState | null,
    issueStage: Stage,
  ): void {
    const stageExecutions = executionRows.filter(row => row.stage === stage);
    const projected = this.buildProjectedStageSeed(stage, stageExecutions, suiteRow, approvalState, issueStage);
    if (!projected) return;

    this.db.transaction(() => {
      this.insertProjectedStageState(issueId, stage, projected, approvalState);
      this.seedStaticTasks(issueId, stage);

      this.projectLegacyBuildTasks(issueId, stage, tasksFile);
      this.projectExecutionEvidence(issueId, stage, stageExecutions);
      this.projectSuiteChecks(issueId, stage, suiteRow);
    });
  }

  private insertProjectedStageState(
    issueId: string,
    stage: Stage,
    projected: ProjectedStageSeed,
    approvalState: LegacyApprovalState | null,
  ): void {
    this.db.run(
      `INSERT OR IGNORE INTO stage_states
       (issue_id, stage, status, started_at, completed_at, approval_status, approval_output, approval_requested_at, approval_responded_at, attempts, updated_at)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
      [
        issueId,
        stage,
        projected.status,
        projected.startedAt,
        projected.completedAt,
        approvalState?.stage === stage ? approvalState.status : null,
        approvalState?.stage === stage && approvalState.output != null ? JSON.stringify(approvalState.output) : null,
        approvalState?.stage === stage ? approvalState.requestedAt ?? null : null,
        approvalState?.stage === stage ? approvalState.respondedAt ?? null : null,
        projected.attempts,
        projected.updatedAt,
      ],
    );
  }

  private projectLegacyBuildTasks(issueId: string, stage: Stage, tasksFile: TasksFile | null): void {
    if (stage !== Stage.Build || !tasksFile) return;

    for (const task of tasksFile.tasks) {
      const status = task.passes
        ? 'completed'
        : task.error
          ? 'failed'
          : 'pending';
      this.upsertTask(issueId, stage, {
        taskId: task.id,
        title: task.title,
        status,
        source: 'dynamic',
        order: task.order,
        attempts: task.attempts,
        output: task.error ? { error: task.error } : undefined,
      });
    }
  }

  private projectExecutionEvidence(
    issueId: string,
    stage: Stage,
    stageExecutions: StageExecutionProjectionRow[],
  ): void {
    for (const execution of stageExecutions) {
      this.projectExecutionTaskEvidence(issueId, stage, execution);
      this.projectExecutionCheckEvidence(issueId, stage, execution);
    }
  }

  private projectExecutionTaskEvidence(
    issueId: string,
    stage: Stage,
    execution: StageExecutionProjectionRow,
  ): void {
    const taskResults = this.parseJson<StageTaskResult[]>(execution.task_results, []);
    for (const result of taskResults) {
      this.upsertTask(issueId, stage, {
        taskId: result.taskId,
        title: result.title,
        status: result.status,
        artifacts: result.artifacts,
        output: result.output,
        attempts: result.attempts,
        duration: result.duration,
      });
    }
  }

  private projectExecutionCheckEvidence(
    issueId: string,
    stage: Stage,
    execution: StageExecutionProjectionRow,
  ): void {
    const checkResults = this.parseJson<CheckResult[]>(execution.check_results, []);
    for (const result of checkResults) {
      this.upsertCheck(issueId, stage, {
        checkName: result.name,
        status: normalizeCheckStatus(result.status),
        message: result.message ?? null,
        output: result.output,
      });
    }
  }

  private projectSuiteChecks(
    issueId: string,
    stage: Stage,
    suiteRow: CheckSuiteProjectionRow | undefined,
  ): void {
    if (stage !== Stage.Check || !suiteRow) return;

    const checks = this.parseJson<CheckSuiteChecks | null>(suiteRow.checks, null);
    if (!checks) return;

    for (const [checkName, state] of Object.entries(checks)) {
      this.upsertCheck(issueId, stage, {
        checkName,
        status: this.normalizeProjectedCheckSuiteStatus(state),
        output: state.output,
        lastRunAt: state.ranAt ?? suiteRow.updated_at,
      });
    }
  }

  private buildProjectedStageSeed(
    stage: Stage,
    stageExecutions: StageExecutionProjectionRow[],
    suiteRow: CheckSuiteProjectionRow | undefined,
    approvalState: LegacyApprovalState | null,
    issueStage: Stage,
  ): ProjectedStageSeed | null {
    const latestExecution = stageExecutions.at(-1);
    const status = this.resolveProjectedStageStatus(stage, latestExecution, suiteRow, approvalState, issueStage);
    const completedAt = status === 'passed' || status === 'failed' || status === 'skipped'
      ? latestExecution?.updated_at ?? null
      : null;
    const startedAt = this.resolveProjectedStageStartedAt(status, latestExecution, suiteRow);
    const updatedAt = suiteRow?.updated_at ?? latestExecution?.updated_at ?? new Date().toISOString();

    if (!latestExecution && !suiteRow && approvalState?.stage !== stage && status === 'pending') {
      return null;
    }

    return {
      status,
      startedAt,
      completedAt,
      attempts: Math.max(0, stageExecutions.length - 1),
      updatedAt,
    };
  }

  private resolveProjectedStageStatus(
    stage: Stage,
    latestExecution: StageExecutionProjectionRow | undefined,
    suiteRow: CheckSuiteProjectionRow | undefined,
    approvalState: LegacyApprovalState | null,
    issueStage: Stage,
  ): StageStateStatus {
    const executionStatus = latestExecution ? normalizeStageStateStatus(latestExecution.status) : null;
    const suiteStatus = stage === Stage.Check && suiteRow ? normalizeStageStateStatus(suiteRow.status) : null;
    const approvalStatus = approvalState?.stage === stage && approvalState.status === 'awaiting'
      ? 'awaiting-approval'
      : null;
    const liveStatus = issueStage === stage ? 'running' : null;

    return approvalStatus ?? suiteStatus ?? liveStatus ?? executionStatus ?? 'pending';
  }

  private resolveProjectedStageStartedAt(
    status: StageStateStatus,
    latestExecution: StageExecutionProjectionRow | undefined,
    suiteRow: CheckSuiteProjectionRow | undefined,
  ): string | null {
    if (latestExecution) return latestExecution.created_at;
    if (status === 'running' || status === 'awaiting-approval') {
      return suiteRow?.updated_at ?? null;
    }
    return null;
  }

  private normalizeProjectedCheckSuiteStatus(state: CheckState): StageCheckStatus {
    return normalizeCheckStatus(state.status);
  }

  private readLegacyTasksFile(issueNumber: number, projectId: string): TasksFile | null {
    const project = this.db.get<ProjectLookupRow>(
      'SELECT id, path FROM projects WHERE id = ?',
      [projectId],
    );
    if (!project) return null;

    const changeDir = findChangeDir(project.path, issueNumber);
    if (!changeDir) return null;

    const tasksPath = path.join(changeDir, 'tasks.json');
    if (!fs.existsSync(tasksPath)) return null;

    try {
      const parsed = JSON.parse(fs.readFileSync(tasksPath, 'utf-8')) as TasksFile;
      return Array.isArray(parsed.tasks) ? parsed : null;
    } catch {
      return null;
    }
  }

  private parseJson<T>(value: string | null, fallback: T): T {
    if (!value) return fallback;
    try {
      return JSON.parse(value) as T;
    } catch {
      return fallback;
    }
  }
}
