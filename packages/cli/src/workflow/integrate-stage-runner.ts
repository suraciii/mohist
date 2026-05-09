import { Stage } from '../types';
import type { StageContext } from './stage-context';
import { BaseStageRunner } from './base-stage-runner';
import { Log } from '../util/log';

const log = Log.create({ service: 'integrate-stage-runner' });

export interface IntegrateStageRunnerOptions {
  worktreePath: string;
}

export class IntegrateStageRunner extends BaseStageRunner {

  constructor(_options: IntegrateStageRunnerOptions) {
    super();
  }

  canHandle(stage: Stage): boolean {
    return stage === Stage.Integrate;
  }

  protected getPreTaskChecks(): import('./checks').Check[] {
    return [];
  }

  protected async executeTasks(ctx: StageContext): Promise<unknown> {
    log.info('Integrate stage tasks placeholder', {
      issueNumber: ctx.issue.number,
    });
    return { integrate: true };
  }

  protected getChecks(): import('./checks').Check[] {
    return [];
  }

  protected getNextStage(): Stage {
    return Stage.Done;
  }
}