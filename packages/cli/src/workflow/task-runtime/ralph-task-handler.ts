import type { StageContext, StageTaskResult } from '../stage-context';
import type { ExecutableTask, RalphTaskInput } from './types';
import {
  executeRalphTask,
  type RalphTaskHandlerDeps,
  type RalphTaskHandlerOptions,
} from '../../openspec/ralph';

export interface RalphTaskRuntimeHandlerDeps extends Omit<RalphTaskHandlerDeps, 'worktreePath'> {
  worktreePath?: string;
  createOptions?: (input: RalphTaskInput, ctx: StageContext) => RalphTaskHandlerOptions;
}

export function createRalphTaskTaskHandler(deps: RalphTaskRuntimeHandlerDeps): (
  task: ExecutableTask,
  ctx: StageContext,
) => Promise<StageTaskResult> {
  return async function runRalphTask(task: ExecutableTask, ctx: StageContext): Promise<StageTaskResult> {
    if (task.kind !== 'ralph-task' || !task.input) {
      throw new Error(`Unsupported task for Ralph handler: ${task.taskId}`);
    }

    const input = task.input as RalphTaskInput;
    const loadedTask = {
      task: input.task,
      change: input.change,
      totalTasks: input.totalTasks,
    };

    const options = deps.createOptions?.(input, ctx) ?? {
      attempt: input.attempt,
      stage: input.stage,
    };

    const result = await executeRalphTask(loadedTask, ctx, options, {
      worktreePath: deps.worktreePath ?? ctx.acpOptions.cwd,
      acpSessionRunner: deps.acpSessionRunner,
      worktreeManager: deps.worktreeManager,
      observers: deps.observers,
      onBeforeKill: deps.onBeforeKill,
    });

    return result.stageTaskResult;
  };
}
