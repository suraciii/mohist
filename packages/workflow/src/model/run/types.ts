import type { WorkflowStageId } from '../workflow-definition';

export type WorkflowRunStatus = 'pending' | 'running' | 'paused' | 'passed' | 'failed' | 'cancelled';
export type StageRunStatus = 'pending' | 'running' | 'awaiting-approval' | 'passed' | 'failed';
export type TaskRunStatus = 'pending' | 'running' | 'completed' | 'failed';
export type CheckRunStatus = 'pending' | 'passed' | 'failed';
export type FailureReason =
  | 'task-failed'
  | 'check-unrepaired'
  | 'approval-rejected';

export interface CausedByMetadata {
  type: 'check-failure' | 'task-failure' | 'branch-changed' | 'conflict' | 'retry' | 'user-action' | 'system-policy';
  checkName?: string;
  taskId?: string;
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
  with?: Record<string, unknown>;
}

export interface TaskResultInput {
  status: 'completed' | 'failed';
  reason?: string;
  causedBy?: CausedByMetadata;
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

export type WorkflowWork =
  | { kind: 'stage-init'; stage: WorkflowStageId; definition: { tasksFrom?: { uses: string; with?: Record<string, unknown> } } }
  | { kind: 'task'; stage: WorkflowStageId; task: { id: string; title: string; uses?: string; with?: Record<string, unknown> } }
  | { kind: 'check'; stage: WorkflowStageId; check: { name: string; title: string; uses?: string; with?: Record<string, unknown> } }
  | { kind: 'await-approval'; stage: WorkflowStageId }
  | { kind: 'complete'; stage: WorkflowStageId }
  | { kind: 'blocked'; stage: WorkflowStageId; reason: StageCompletionGuard }
  | { kind: 'failed'; reason: FailureDetails };

export type StageCompletionGuard =
  | { complete: false; reason: 'workflow-not-running'; stage: WorkflowStageId }
  | { complete: false; reason: 'stage-failed'; stage: WorkflowStageId }
  | { complete: false; reason: 'stage-not-running'; stage: WorkflowStageId };

export interface TaskRunState {
  id: string;
  title: string;
  uses?: string;
  with?: Record<string, unknown>;
  status: TaskRunStatus;
}

export interface CheckRunState {
  name: string;
  title: string;
  uses?: string;
  with?: Record<string, unknown>;
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
  tasks: TaskRunState[];
  checks: CheckRunState[];
  approval: ApprovalState | null;
  failure: FailureDetails | null;
}

export interface WorkflowRunState {
  id: string;
  status: WorkflowRunStatus;
  currentStage: WorkflowStageId;
  stageOrder: WorkflowStageId[];
  stageRuns: StageRunState[];
  failure: FailureDetails | null;
  pauseRequested?: boolean;
}
