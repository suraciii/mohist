import type { StageContext } from '../stage-context';
import type { TaskLoader } from './task-loader-registry';
import type { ExecutableTask, RalphTaskInput } from './types';
import { detectOpenSpecChange } from '../../openspec/detector';
import { readTasks } from '../../openspec/ralph-executor';

export function createRalphTaskLoader(): TaskLoader {
  return {
    kind: 'ralph',
    load(ctx: StageContext): ExecutableTask[] {
      const change = detectOpenSpecChange(ctx.acpOptions.cwd, ctx.issue);
      if (!change) return [];

      const tasks = readTasks(change.tasksPath);
      if (!tasks) return [];

      return tasks.map(task => {
        const input: RalphTaskInput = {
          taskId: task.id,
          title: task.title,
          task,
          change,
          totalTasks: tasks.length,
          stage: 'build',
          attempt: task.attempts + 1,
        };
        return {
          taskId: task.id,
          title: task.title,
          kind: 'ralph-task' as const,
          uses: 'mohist/ralph-tasks',
          input,
        };
      });
    },
  };
}
