import type { OpenSpecChange } from '../detector';
import type { Task } from '../context-assembler';
import { readTasks, sortTasksByOrder, validateTaskDependencies } from './task-utils';
import { type DependencyValidationResult } from './types';
import type { ExecutableTask, RalphTaskInput } from '../../workflow/tasks/types';

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
  executableTasks: ExecutableTask[];
  sortedTasks: Task[];
  validation: DependencyValidationResult;
}

export class RalphTaskLoader {
  load(change: OpenSpecChange, options?: RalphTaskLoaderOptions): RalphTaskLoaderResult {
    const rawTasks = readTasks(change.tasksPath);
    if (!rawTasks || rawTasks.length === 0) {
      return {
        tasks: [],
        executableTasks: [],
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

    const executableTasks: ExecutableTask[] = loadedTasks.map((loadedTask) => {
      const input: RalphTaskInput = {
        taskId: loadedTask.task.id,
        title: loadedTask.task.title,
        task: loadedTask.task,
        change: loadedTask.change,
        totalTasks: loadedTask.totalTasks,
        stage: 'build',
        attempt: loadedTask.task.attempts + 1,
      };

      return {
        taskId: loadedTask.task.id,
        title: loadedTask.task.title,
        kind: 'ralph-task',
        input,
      };
    });

    return {
      tasks: loadedTasks,
      executableTasks,
      sortedTasks,
      validation,
    };
  }
}
