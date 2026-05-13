import type { StageContext, StageTaskResult } from '../stage-context';
import type { ServiceCallTaskInput } from './types';
import { emitStageTaskUpdate } from '../stage-context';
import { Log } from '../../util/log';

const log = Log.create({ service: 'service-call-task-handler' });

export function createServiceCallTaskHandler(): (
  input: ServiceCallTaskInput,
  ctx: StageContext,
) => Promise<StageTaskResult> {
  return async function runServiceCallTask(
    input: ServiceCallTaskInput,
    ctx: StageContext,
  ): Promise<StageTaskResult> {
    const startedAt = Date.now();
    const { taskId, title, serviceFn, stage, attempt } = input;

    emitStageTaskUpdate(
      ctx.eventBus,
      ctx.issue.id,
      ctx.issue.projectId,
      stage,
      taskId,
      title,
      'started',
      attempt,
      [],
    );

    try {
      const result = await serviceFn(ctx);
      const duration = Date.now() - startedAt;

      emitStageTaskUpdate(
        ctx.eventBus,
        ctx.issue.id,
        ctx.issue.projectId,
        stage,
        taskId,
        title,
        'completed',
        attempt,
        [],
      );

      return {
        taskId,
        title,
        status: 'completed',
        artifacts: [],
        attempts: attempt,
        duration,
        output: {
          kind: 'service-call-task',
          stage,
          attempt,
          success: true,
          result,
          summary: `${title} completed`,
        },
      };
    } catch (err) {
      const duration = Date.now() - startedAt;
      const error = err instanceof Error ? err.message : String(err);
      log.warn('Service call task failed', { taskId, title, stage, error });

      emitStageTaskUpdate(
        ctx.eventBus,
        ctx.issue.id,
        ctx.issue.projectId,
        stage,
        taskId,
        title,
        'failed',
        attempt,
        [],
      );

      return {
        taskId,
        title,
        status: 'failed',
        artifacts: [],
        attempts: attempt,
        duration,
        output: {
          kind: 'service-call-task',
          stage,
          attempt,
          success: false,
          error,
          summary: `${title} failed: ${error}`,
        },
      };
    }
  };
}

export const defaultServiceCallTaskHandler = createServiceCallTaskHandler();