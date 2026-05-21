export type TaskExecutionStatus = 'completed' | 'failed' | 'skipped';

export interface TaskInputDefinition {
  description?: string;
  required?: boolean;
  default?: unknown;
}

export interface TaskOutputDefinition {
  description?: string;
}

export interface TaskMetadata {
  id: string;
  name?: string;
  description?: string;
  inputs?: Record<string, TaskInputDefinition>;
  outputs?: Record<string, TaskOutputDefinition>;
}

export interface TaskExecutionContext {
  taskId: string;
  title: string;
  attempt: number;
  cwd: string;
  with: Record<string, unknown>;
  workflow: {
    stage: string;
    issueId: string;
    issueNumber: number;
    artifacts: Record<string, string>;
  };
  log: {
    debug(message: string, data?: Record<string, unknown>): void;
    info(message: string, data?: Record<string, unknown>): void;
    warn(message: string, data?: Record<string, unknown>): void;
    error(message: string, data?: Record<string, unknown>): void;
  };
}

export interface TaskExecutionResult {
  status: TaskExecutionStatus;
  output?: unknown;
  artifacts?: string[];
  events?: string[];
  reason?: string;
  error?: string;
}

export interface TaskProvider {
  id: string;
  metadata?: TaskMetadata;
  run(ctx: TaskExecutionContext): Promise<TaskExecutionResult>;
}

export interface TaskDefinition {
  taskId: string;
  title: string;
}

export interface ExecutableTask {
  taskId: string;
  title: string;
  uses?: string;
  prompt?: string;
  input?: unknown;
  artifactVerification?: (artifacts: string[]) => string[];
}
