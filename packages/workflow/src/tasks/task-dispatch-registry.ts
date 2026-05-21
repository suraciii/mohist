import type { CheckResult, StageContext, StageTaskResult } from '../runtime';
import type { TaskDefinition } from '../model';
import type { ExecutableTask } from './types';

export interface TaskDispatchInput {
  ctx: StageContext;
  task: ExecutableTask;
  attempt: number;
  failedCheck?: CheckResult;
  worktreePath: string;
  agentSessionRef?: string;
  sourceTask?: TaskDefinition;
}

export interface TaskDispatchProvider {
  id: string;
  run(input: TaskDispatchInput): Promise<StageTaskResult | null>;
}

export interface TaskDispatchRegistry {
  run(input: TaskDispatchInput): Promise<StageTaskResult | null>;
  get(id: string): TaskDispatchProvider | undefined;
  register(provider: TaskDispatchProvider): void;
}

export function createTaskDispatchRegistry(providers: TaskDispatchProvider[] = []): TaskDispatchRegistry {
  const map = new Map<string, TaskDispatchProvider>();
  for (const provider of providers) {
    map.set(provider.id, provider);
  }

  return {
    run(input) {
      const providerId = input.sourceTask?.uses ?? input.task.uses ?? '';
      return map.get(providerId)?.run(input) ?? Promise.resolve(null);
    },
    get(id) {
      return map.get(id);
    },
    register(provider) {
      map.set(provider.id, provider);
    },
  };
}
