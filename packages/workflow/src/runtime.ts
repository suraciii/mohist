import type { WorkflowStageId } from './model';

export interface WorkflowIssue {
  id: string;
  number: number;
  title: string;
  stage: WorkflowStageId;
  status: string;
  projectId: string;
}

export interface WorkflowEventBus {
  emit(event: string, data: unknown): void;
}

export interface WorkflowContext {
  issue: WorkflowIssue;
  vars: Record<string, unknown>;
  eventBus: WorkflowEventBus;
}

export interface CheckResult {
  name: string;
  status: 'pass' | 'fail' | 'error' | 'pending';
  message?: string;
  output?: unknown;
}

export interface StageTaskResult {
  taskId: string;
  title: string;
  status: 'completed' | 'failed' | 'skipped';
  artifacts: string[];
  events?: string[];
  output?: unknown;
  attempts: number;
  duration: number;
  reason?: string;
}
