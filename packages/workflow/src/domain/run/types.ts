import type { WorkflowStageId } from '../workflow-definition';

export type WorkflowRunStatus = 'pending' | 'running' | 'paused' | 'passed' | 'failed' | 'cancelled';
export type StageRunStatus = 'pending' | 'running' | 'awaiting-approval' | 'passed' | 'failed';
export type TaskRunStatus = 'pending' | 'running' | 'completed' | 'failed';
export type CheckRunStatus = 'pending' | 'passed' | 'failed';
export type FailureReason = 'task-failed' | 'check-unrepaired' | 'approval-rejected';

export interface FailureDetails {
  reason: FailureReason;
  stage: WorkflowStageId;
  taskId?: string;
  checkName?: string;
  message?: string;
}

export interface ApprovalInput {
  output?: unknown;
}

export interface MaterializedTaskInput {
  id: string;
  title: string;
  uses?: string;
  with?: Record<string, unknown>;
}

export interface TaskResult {
  status: 'completed' | 'failed';
  reason?: string;
}

export interface CheckResult {
  name: string;
  status: 'pass' | 'fail' | 'error' | 'pending';
  message?: string;
  output?: unknown;
}

export type StageWork =
  | { kind: 'stage-init'; definition: { tasksFrom?: { uses: string; with?: Record<string, unknown> } } }
  | { kind: 'task'; task: { id: string; title: string; uses?: string; with?: Record<string, unknown> } }
  | { kind: 'check'; check: { name: string; title: string; uses?: string; with?: Record<string, unknown> } }
  | { kind: 'await-approval' }
  | { kind: 'complete' }
  | { kind: 'blocked'; reason: string };

export type WorkflowWork =
  | { kind: 'stage-init'; stage: WorkflowStageId; definition: { tasksFrom?: { uses: string; with?: Record<string, unknown> } } }
  | { kind: 'task'; stage: WorkflowStageId; task: { id: string; title: string; uses?: string; with?: Record<string, unknown> } }
  | { kind: 'check'; stage: WorkflowStageId; check: { name: string; title: string; uses?: string; with?: Record<string, unknown> } }
  | { kind: 'await-approval'; stage: WorkflowStageId }
  | { kind: 'complete'; stage: WorkflowStageId }
  | { kind: 'blocked'; stage: WorkflowStageId; reason: string }
  | { kind: 'failed'; reason: FailureDetails };

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
