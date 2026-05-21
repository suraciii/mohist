import {
  createTaskHandlerRegistry,
  type ExecutableTask,
  type ProviderTaskInput,
  type TaskHandler,
  type TaskHandlerRegistry,
  type TaskKind,
} from './types';
import type { StageContext } from '../stage-context';
import {
  createAgentSessionTaskHandler,
  type AgentSessionTaskHandlerDeps,
} from './agent-session-task-handler';
import { createServiceCallTaskHandler } from './service-call-task-handler';
import {
  createRalphTaskTaskHandler,
  type RalphTaskRuntimeHandlerDeps,
} from './ralph-task-handler';

export interface DefaultTaskHandlerRegistryOptions {
  agentSession?: AgentSessionTaskHandlerDeps;
  ralphTask?: RalphTaskRuntimeHandlerDeps;
}

export function createDefaultTaskHandlerRegistry(
  options: DefaultTaskHandlerRegistryOptions = {},
): TaskHandlerRegistry {
  const handlers: Partial<Record<TaskKind, TaskHandler>> = {
    'agent-session': async (task: ExecutableTask, ctx: StageContext) => {
      if (!task.input) {
        throw new Error(`Missing input for task: ${task.taskId}`);
      }
      return createAgentSessionTaskHandler(options.agentSession)(task.input as any, ctx);
    },
    'service-call': async (task: ExecutableTask, ctx: StageContext) => {
      if (!task.input) {
        throw new Error(`Missing input for task: ${task.taskId}`);
      }
      return createServiceCallTaskHandler()(task.input as any, ctx);
    },
    'provider-task': async (task: ExecutableTask, ctx: StageContext) => {
      if (!task.input || typeof (task.input as { run?: unknown }).run !== 'function') {
        throw new Error(`Missing provider task input for task: ${task.taskId}`);
      }
      return (task.input as ProviderTaskInput).run(ctx);
    },
  };

  if (options.ralphTask) {
    handlers['ralph-task'] = createRalphTaskTaskHandler(options.ralphTask);
  }

  return createTaskHandlerRegistry(handlers);
}
