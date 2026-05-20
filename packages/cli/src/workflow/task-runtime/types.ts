import type { StageContext, StageTaskResult } from '../stage-context';
import type { RequiredMarkerDefinition } from '../domain';

export type TaskKind = 'agent-session' | 'service-call' | 'ralph-task';

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
  emits?: string[];
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
  emits?: string[];
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
