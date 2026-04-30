import { Stage } from '../types';
import type { StageContext, StageRunResult } from './stage-context';
import type { Check, CheckContext } from './checks';

export interface StageRunner {
  canHandle(stage: Stage): boolean;
  run(ctx: StageContext): Promise<StageRunResult>;
}

export class CheckStageRunner implements StageRunner {
  private checks: Check[];

  constructor(checks: Check[]) {
    this.checks = checks;
  }

  canHandle(stage: Stage): boolean {
    return stage === Stage.Check;
  }

  async run(ctx: StageContext): Promise<StageRunResult> {
    const changeDir = ctx.artifactManager.getChangeDir(ctx.issue.number);
    if (!changeDir) {
      return {
        success: false,
        requiresApproval: false,
        output: null,
        message: `Change directory not found for issue #${ctx.issue.number}`,
      };
    }

    const checkContext: CheckContext = {
      issue: ctx.issue,
      changeDir,
      eventBus: ctx.eventBus,
      projectId: ctx.issue.projectId,
      acpOptions: ctx.acpOptions,
    };

    const results = [];

    for (const check of this.checks) {
      const result = await check.run(checkContext);
      results.push(result);

      if (result.status === 'fail' && check.name === 'BuildTestCheck') {
        return {
          success: false,
          requiresApproval: false,
          output: { checkResults: results },
          message: result.message ?? 'Build test failed',
          nextStage: Stage.Build,
        };
      }
    }

    const allPassed = results.every((r) => r.status === 'pass');
    const anyError = results.some((r) => r.status === 'error');

    if (anyError) {
      return {
        success: false,
        requiresApproval: false,
        output: { checkResults: results },
        message: 'Check stage encountered errors',
      };
    }

    if (!allPassed) {
      return {
        success: false,
        requiresApproval: false,
        output: { checkResults: results },
        message: 'One or more checks failed',
      };
    }

    return {
      success: true,
      requiresApproval: true,
      output: { checkResults: results },
      message: 'All checks passed, awaiting user approval',
    };
  }
}