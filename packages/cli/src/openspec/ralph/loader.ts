import type { OpenSpecChange } from '../detector';
import type { Task } from '../context-assembler';
import { readTasks, sortTasksByOrder, validateTaskDependencies } from './task-utils';
import { type DependencyValidationResult } from './types';

export { type DependencyValidationResult } from './types';
export { sortTasksByOrder, readTasks } from './task-utils';

export interface RalphTaskLoaderOptions {
  ignoreTaskFileProgress?: boolean;
}

export interface RalphLoadedTask {
  task: Task;
  totalTasks: number;
  change: OpenSpecChange;
}

export interface RalphTaskLoaderResult {
  tasks: RalphLoadedTask[];
  sortedTasks: Task[];
  validation: DependencyValidationResult;
}

export class RalphTaskLoader {
  load(change: OpenSpecChange, options?: RalphTaskLoaderOptions): RalphTaskLoaderResult {
    const rawTasks = readTasks(change.tasksPath);
    if (!rawTasks || rawTasks.length === 0) {
      return {
        tasks: [],
        sortedTasks: [],
        validation: { valid: true, errors: [] },
      };
    }

    if (options?.ignoreTaskFileProgress) {
      for (const task of rawTasks) {
        task.passes = false;
        task.error = null;
      }
    }

    const validation = validateTaskDependencies(rawTasks);
    const sortedTasks = sortTasksByOrder(rawTasks);

    const loadedTasks: RalphLoadedTask[] = sortedTasks.map(task => ({
      task,
      totalTasks: sortedTasks.length,
      change,
    }));

    return {
      tasks: loadedTasks,
      sortedTasks,
      validation,
    };
  }
}
