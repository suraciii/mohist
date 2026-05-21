import type { StageContext } from '../runtime';
import type { ExecutableTask } from './types';

export type TaskLoaderKind = 'static' | 'openspec' | 'runtime';

export interface TaskLoader {
  kind: TaskLoaderKind;
  load(ctx: StageContext): ExecutableTask[];
}

export interface TaskLoaderRegistry {
  get(kind: TaskLoaderKind): TaskLoader | undefined;
  list(): TaskLoader[];
}

export function createTaskLoaderRegistry(loaders: TaskLoader[]): TaskLoaderRegistry {
  const map = new Map<TaskLoaderKind, TaskLoader>();
  for (const loader of loaders) {
    map.set(loader.kind, loader);
  }
  return {
    get(kind) {
      return map.get(kind);
    },
    list() {
      return [...loaders];
    },
  };
}
