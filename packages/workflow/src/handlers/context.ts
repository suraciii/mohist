import type { WorkflowStageId } from '../domain';

export interface WorkflowContext {
  issue: WorkflowIssue;
  vars: Record<string, unknown>;
  eventBus: WorkflowEventBus;
}

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

export interface WorkflowTaskInput {
  id: string;
  title: string;
  with?: Record<string, unknown>;
}

export interface WorkflowCheckInput {
  name: string;
  title: string;
  with?: Record<string, unknown>;
}
