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
