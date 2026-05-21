import type { WorkflowStageId } from '../workflow-definition';

export type WorkflowRunStatus = 'pending' | 'running' | 'passed' | 'failed' | 'cancelled';
export type StageRunStatus = 'pending' | 'running' | 'awaiting-approval' | 'passed' | 'failed' | 'skipped';
export type TaskRunStatus = 'pending' | 'running' | 'completed' | 'failed';
export type CheckRunStatus = 'pending' | 'passed' | 'failed';
export type WorkflowRecoverySummary = 'running' | 'awaiting-approval' | 'waiting-for-recovery' | 'completed';
export type FailureReason =
  | 'task-failed'
  | 'check-unrepaired'
  | 'approval-rejected'
  | 'post-commit-check-failed'
  | 'work-interrupted';

export interface CausedByMetadata {
  type: 'check-failure' | 'task-failure' | 'branch-changed' | 'conflict' | 'retry' | 'user-action' | 'system-policy';
  checkName?: string;
  taskId?: string;
  message?: string;
}

export interface TaskResetMetadata {
  type: 'workflow-policy';
  taskId?: string;
  eventName?: string;
  message?: string;
}

export interface FailureDetails {
  reason: FailureReason;
  stage: WorkflowStageId;
  taskId?: string;
  checkName?: string;
  message?: string;
  causedBy?: CausedByMetadata;
}

export interface MaterializedTaskInput {
  id: string;
  title: string;
  uses?: string;
  order?: number;
  dependsOn?: string[];
}

export type WorkSourceState =
  | { evaluated: true; tasks: MaterializedTaskInput[] }
  | { evaluated: true; missing: true }
  | { evaluated: true; invalid: true }
  | { evaluated: true; empty: true }
  | { evaluated: false };

export interface CommitPoint {
  taskId?: string;
  checkName?: string;
  uses?: string;
  metadata: Record<string, unknown>;
  createdAt: string;
}

export interface TaskResultInput {
  status: 'completed' | 'failed';
  attempts?: number;
  duration?: number;
  artifacts?: string[];
  output?: unknown;
  reason?: string;
  causedBy?: CausedByMetadata;
  events?: string[];
}

export interface CheckResultInput {
  name: string;
  status: 'pass' | 'fail' | 'error' | 'pending';
  message?: string;
  output?: unknown;
}

export interface ApprovalInput {
  output?: unknown;
}

export type WorkflowEvent =
  | { type: 'workflow-started'; stage: WorkflowStageId }
  | { type: 'stage-started'; stage: WorkflowStageId }
  | { type: 'stage-retried'; stage: WorkflowStageId }
  | { type: 'task-completed'; stage: WorkflowStageId; taskId: string }
  | { type: 'task-failed'; stage: WorkflowStageId; taskId: string; reason: FailureDetails }
  | { type: 'task-invalidated'; stage: WorkflowStageId; taskId: string; reason: string }
  | { type: 'check-invalidated'; stage: WorkflowStageId; checkName: string; reason: string }
  | { type: 'check-recorded'; stage: WorkflowStageId; checkName: string; status: CheckRunStatus }
  | { type: 'retry-task-scheduled'; stage: WorkflowStageId; taskId: string; causedBy: CausedByMetadata }
  | { type: 'approval-requested'; stage: WorkflowStageId }
  | { type: 'approval-approved'; stage: WorkflowStageId }
  | { type: 'approval-rejected'; stage: WorkflowStageId; reason: FailureDetails }
  | { type: 'stage-completed'; stage: WorkflowStageId }
  | { type: 'stage-failed'; stage: WorkflowStageId; reason: FailureDetails }
  | { type: 'workflow-completed' }
  | { type: 'workflow-failed'; reason: FailureDetails }
  | { type: 'commit-point-created'; stage: WorkflowStageId; commitPoint: CommitPoint };

export type WorkflowWork =
  | { kind: 'task-source'; stage: WorkflowStageId }
  | { kind: 'task'; stage: WorkflowStageId; taskId: string }
  | { kind: 'check'; stage: WorkflowStageId; checkName: string }
  | { kind: 'await-approval'; stage: WorkflowStageId }
  | { kind: 'complete' }
  | { kind: 'blocked'; stage: WorkflowStageId; reason: StageCompletionGuard }
  | { kind: 'failed'; reason: FailureDetails };

export type StageCompletionGuard =
  | { complete: true }
  | { complete: false; reason: 'missing-static-task'; taskId: string }
  | { complete: false; reason: 'missing-static-check'; checkName: string }
  | { complete: false; reason: 'static-task-not-successful'; taskId: string; status: TaskRunStatus }
  | { complete: false; reason: 'static-check-not-passed'; checkName: string }
  | { complete: false; reason: 'run-task-pending'; taskId: string }
  | { complete: false; reason: 'run-task-failed'; taskId: string }
  | { complete: false; reason: 'dynamic-source-not-evaluated'; stage: WorkflowStageId }
  | { complete: false; reason: 'dynamic-source-missing'; stage: WorkflowStageId }
  | { complete: false; reason: 'dynamic-source-invalid'; stage: WorkflowStageId }
  | { complete: false; reason: 'dynamic-source-empty'; stage: WorkflowStageId }
  | { complete: false; reason: 'commit-evidence-missing'; stage: WorkflowStageId; taskId?: string; checkName?: string; uses?: string }
  | { complete: false; reason: 'approval-required'; stage: WorkflowStageId }
  | { complete: false; reason: 'workflow-not-running'; stage: WorkflowStageId }
  | { complete: false; reason: 'missing-current-stage'; stage: WorkflowStageId }
  | { complete: false; reason: 'stage-failed'; stage: WorkflowStageId }
  | { complete: false; reason: 'stage-not-running'; stage: WorkflowStageId };

export interface WorkflowDecision {
  events: WorkflowEvent[];
  nextWork: WorkflowWork;
}

export interface TaskRunState {
  id: string;
  title: string;
  uses?: string;
  status: TaskRunStatus;
}

export interface CheckRunState {
  name: string;
  title: string;
  status: CheckRunStatus;
  message: string | null;
  output: unknown | null;
}

export interface ApprovalState {
  status: 'awaiting' | 'approved' | 'rejected';
  output: unknown | null;
  requestedAt: string;
  respondedAt: string | null;
}

export interface StageRunState {
  stage: WorkflowStageId;
  status: StageRunStatus;
  order: number;
  attemptSequence?: number;
  tasks: TaskRunState[];
  checks: CheckRunState[];
  approval: ApprovalState | null;
  failure: FailureDetails | null;
  commitPoint: CommitPoint | null;
  workSourceState?: WorkSourceState;
}

export interface WorkflowRunState {
  id: string;
  status: WorkflowRunStatus;
  currentStage: WorkflowStageId;
  stageOrder: WorkflowStageId[];
  stageRuns: StageRunState[];
  failure: FailureDetails | null;
}

export function baseRuntimeTaskId(taskId: string): string {
  return taskId.replace(/:\d+$/, '');
}

export function escapeRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}
