import type { StageContext, StageTaskResult } from '../stage-context';
import type { RequiredMarkerDefinition } from './agent-required-markers';

export type TaskKind = 'agent-session' | 'service-call' | 'ralph-task';
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

export interface RalphTaskInput {
  taskId: string;
  title: string;
  task: import('../../openspec/context-assembler').Task;
  change: import('../../openspec/detector').OpenSpecChange;
  totalTasks: number;
  stage: string;
  attempt: number;
}

export interface TaskDefinition {
  taskId: string;
  title: string;
  kind: TaskKind;
}

export interface ExecutableTask {
  taskId: string;
  title: string;
  kind: TaskKind;
  uses?: string;
  prompt?: string;
  input?: unknown;
  artifactVerification?: (artifacts: string[]) => string[];
}

export interface AgentSessionTaskInput {
  taskId: string;
  title: string;
  prompt: string;
  cwd: string;
  stage: string;
  attempt: number;
  agentSessionRef?: string;
  artifactVerification?: (artifacts: string[]) => string[];
  retryPromptFactory?: (ctx: StageContext, attempt: number) => string | null;
  requiredMarkers?: RequiredMarkerDefinition[];
}

export interface ServiceCallTaskInput {
  taskId: string;
  title: string;
  serviceFn: (ctx: StageContext) => Promise<unknown>;
  stage: string;
  attempt: number;
}

export type TaskHandler = (
  task: ExecutableTask,
  ctx: StageContext,
) => Promise<StageTaskResult>;

export type AgentSessionTaskHandler = (
  input: AgentSessionTaskInput,
  ctx: StageContext,
) => Promise<StageTaskResult>;

export type ServiceCallTaskHandler = (
  input: ServiceCallTaskInput,
  ctx: StageContext,
) => Promise<StageTaskResult>;

export type RalphTaskHandler = (
  input: RalphTaskInput,
  ctx: StageContext,
) => Promise<StageTaskResult>;

export interface TaskHandlerRegistry {
  get(kind: TaskKind): TaskHandler | undefined;
  register(kind: TaskKind, handler: TaskHandler): void;
}

export function createTaskHandlerRegistry(
  handlers: Partial<Record<TaskKind, TaskHandler>>,
): TaskHandlerRegistry {
  const map = new Map<TaskKind, TaskHandler>(Object.entries(handlers) as [TaskKind, TaskHandler][]);
  return {
    get(kind) {
      return map.get(kind);
    },
    register(kind, handler) {
      map.set(kind, handler);
    },
  };
}
