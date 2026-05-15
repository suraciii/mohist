import type { StageContext, StageTaskResult } from '../stage-context';
import { Stage } from '../../types';
import type { ExecutableTask, RalphTaskInput } from './types';
import {
  executeRalphTask,
  type RalphTaskHandlerDeps,
  type RalphTaskHandlerOptions,
} from '../../openspec/ralph';
import { runRalphLoop, type RalphExecutorContext, type RalphExecutorOptions } from '../../openspec/ralph-executor';
import { detectOpenSpecChange } from '../../openspec/detector';
import { readTasks } from '../../openspec/ralph-executor';
import { Log } from '../../util/log';

const log = Log.create({ service: 'ralph-task-handler' });

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

export function createRalphTaskHandler(): (
  task: ExecutableTask,
  ctx: StageContext,
) => Promise<StageTaskResult> {
  return async function runRalphTask(
    task: ExecutableTask,
    ctx: StageContext,
  ): Promise<StageTaskResult> {
    const taskId = task.taskId;
    const requestedTaskId = typeof task.input === 'string'
      ? task.input
      : (task.input as RalphTaskInput | undefined)?.taskId ?? task.taskId;

    const change = detectOpenSpecChange(ctx.acpOptions.cwd, ctx.issue);
    if (!change) {
      log.warn('Ralph task handler: detectOpenSpecChange returned null', {
        worktreePath: ctx.acpOptions.cwd,
        issueNumber: ctx.issue.number,
      });
      return {
        taskId,
        title: task.title,
        status: 'failed',
        artifacts: [],
        attempts: 1,
        duration: 0,
        reason: `No OpenSpec change found for issue #${ctx.issue.number}`,
      };
    }

    const activeStageExecution = ctx.stageExecutionRepo?.findActiveByIssueId?.(ctx.issue.id);
    const stageExecutionId = activeStageExecution?.stage === Stage.Build
      ? activeStageExecution.id
      : ctx.stageExecutionRepo?.create(ctx.issue.id, Stage.Build).id;

    const ralphContext: RalphExecutorContext = {
      worktreePath: ctx.acpOptions.cwd,
      projectPath: ctx.acpOptions.cwd,
      issueId: ctx.issue.id,
      projectId: ctx.issue.projectId,
      eventBus: ctx.eventBus,
      executionId: `build-${ctx.issue.number}`,
      issueNumber: ctx.issue.number,
      issueTitle: ctx.issue.title,
      issueBody: ctx.issue.body,
      stage: Stage.Build,
      model: ctx.acpOptions.model,
      stageExecutionId,
      stageExecutionRepo: ctx.stageExecutionRepo,
      workflowLogRepo: ctx.workflowLogRepo,
      sessionStreamLogRepo: ctx.sessionStreamLogRepo,
      coderSessionRepo: ctx.coderSessionRepo,
      observers: ctx.acpOptions.observers,
      syncTasksToStageState: ctx.artifactManager
        ? () => ctx.artifactManager.syncTasksToStageState(ctx.issue.number, ctx.issue.id, Stage.Build, ctx.stageStateService!)
        : undefined,
      workflowApplicationService: ctx.workflowApplicationService,
    };

    const options: RalphExecutorOptions = {
      onlyTaskId: requestedTaskId,
      onTaskCompleted: (completedTaskId: string) => {
        if (ctx.checkpointManager) {
          ctx.checkpointManager.markStepComplete(ctx.issue.number, 'build', completedTaskId);
        }
      },
    };

    let loopResult: Awaited<ReturnType<typeof runRalphLoop>> | undefined;

    try {
      loopResult = await runRalphLoop(change, ralphContext, options);
    } catch (err) {
      const error = err instanceof Error ? err.message : String(err);
      log.error('Ralph task handler: runRalphLoop failed', { taskId, error });
      return {
        taskId,
        title: task.title,
        status: 'failed',
        artifacts: [],
        attempts: 1,
        duration: 0,
        reason: error,
      };
    }

    const taskResult = loopResult.taskResults.find(r => r.taskId === requestedTaskId);

    if (!taskResult) {
      return {
        taskId,
        title: task.title,
        status: 'failed',
        artifacts: [],
        attempts: 1,
        duration: 0,
        reason: `Task ${requestedTaskId} not found in Ralph loop results`,
      };
    }

    return {
      taskId: taskResult.taskId,
      title: task.title,
      status: taskResult.status === 'completed' ? 'completed' : 'failed',
      artifacts: [],
      attempts: taskResult.attempts,
      duration: 0,
      reason: taskResult.error,
      alreadyReported: Boolean(ctx.workflowApplicationService),
      output: {
        kind: 'ralph-task',
        stage: Stage.Build,
        success: taskResult.status === 'completed',
        error: taskResult.error,
      },
    };
  };
}

export function materializeRalphTasks(ctx: StageContext): void {
  const change = detectOpenSpecChange(ctx.acpOptions.cwd, ctx.issue);
  if (!change) {
    log.warn('materializeRalphTasks: detectOpenSpecChange returned null', {
      worktreePath: ctx.acpOptions.cwd,
      issueNumber: ctx.issue.number,
    });
    return;
  }

  const tasks = readTasks(change.tasksPath);
  if (!tasks || tasks.length === 0) {
    return;
  }

  if (ctx.workflowApplicationService) {
    ctx.workflowApplicationService.materializeTasks({
      issueId: ctx.issue.id,
      stage: Stage.Build,
      tasks: tasks.map(t => ({
        id: t.id,
        title: t.title,
        order: t.order,
        dependsOn: t.dependsOn ?? [],
      })),
      tasksPath: change.tasksPath,
    });
  }
}
