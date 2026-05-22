import type { TaskResult } from '../domain';

export interface WorkflowTaskInput {
  id: string;
  title: string;
  with?: Record<string, unknown>;
}

export interface TaskHandler {
  run(input: WorkflowTaskInput): Promise<TaskResult>;
}
