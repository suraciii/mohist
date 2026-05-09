import { Stage } from '../types';
import type { StageContext, StageRunResult, CheckResult, StageTaskResult } from './stage-context';
import type { StageRunner } from './check-stage-runner';
import type { Check, CheckContext } from './checks';
import type { StageExecutionStatus } from '../db/stage-execution-repo';
import { Log } from '../util/log';
import { parseVerdict, parseDimensions, readReportFile } from './utils';

const log = Log.create({ service: 'base-stage-runner' });

export abstract class BaseStageRunner implements StageRunner {
  abstract canHandle(stage: Stage): boolean;
  protected abstract executeTasks(ctx: StageContext): Promise<unknown>;
  protected abstract getChecks(): Check[];

  protected getPreTaskChecks(): Check[] {
    return [];
  }

  protected abstract getNextStage(): Stage;

  private stageExecutionId?: string;

  protected getStageExecutionId(): string | undefined {
    return this.stageExecutionId;
  }

  protected appendTaskResult(ctx: StageContext, result: StageTaskResult): void {
    if (!this.stageExecutionId || !ctx.stageExecutionRepo) return;
    try {
      ctx.stageExecutionRepo.appendTaskResult(this.stageExecutionId, result);
    } catch (e) {
      log.warn('appendTaskResult failed', { error: e instanceof Error ? e.message : String(e) });
    }
  }

  async run(ctx: StageContext): Promise<StageRunResult> {
    this.stageExecutionId = undefined;

    if (ctx.stageExecutionRepo) {
      try {
        const execution = ctx.stageExecutionRepo.create(ctx.issue.id, ctx.issue.stage);
        this.stageExecutionId = execution.id;
      } catch (e) {
        log.warn('create stage execution failed', { error: e instanceof Error ? e.message : String(e) });
      }
    }

    let checkResults: CheckResult[] = [];
    const preTaskChecks = this.getPreTaskChecks();
    if (preTaskChecks.length > 0) {
      const preTaskResult = await this.runChecksPhase(ctx, [], preTaskChecks, null, 0, true);
      checkResults = preTaskResult.checkResults ?? [];
      if (!preTaskResult.success) {
        this.updateStageExecutionStatus(ctx, 'failed');
        return preTaskResult;
      }
    }

    let taskOutput: unknown;
    try {
      taskOutput = await this.executeTasks(ctx);
    } catch (err: any) {
      const checkResults: CheckResult[] = [];
      this.persistCheckResults(ctx, checkResults);
      this.updateStageExecutionStatus(ctx, 'failed');
      return {
        success: false,
        output: null,
        checkResults,
        message: `Task execution failed: ${err.message}`,
      };
    }

    const postTaskChecks = this.getChecks();
    const result = await this.runChecksPhase(ctx, checkResults, postTaskChecks, taskOutput, 0, false);
    if (result.success) {
      this.updateStageExecutionStatus(ctx, 'passed');
    } else {
      const hasApproval = result.checkResults?.some(
        (cr: CheckResult) => cr.status === 'fail' && result.message?.includes('approval')
      );
      this.updateStageExecutionStatus(ctx, hasApproval ? 'awaiting-approval' : 'failed');
    }
    return result;
  }

  private async runChecksPhase(
    ctx: StageContext,
    priorResults: CheckResult[],
    checks: Check[],
    taskOutput: unknown,
    taskRetryCount: number,
    isPreTask: boolean,
  ): Promise<StageRunResult> {
    const checkCtx = this.buildCheckContext(ctx);
    const results: CheckResult[] = [...priorResults];

    for (const check of checks) {
      const result = await check.run(checkCtx);
      results.push(result);

      if (result.status !== 'pass') {
        this.persistCheckResults(ctx, results);
        return this.dispatchReaction(ctx, check, result, results, taskOutput, taskRetryCount, isPreTask, checks);
      }
    }

    this.persistCheckResults(ctx, results);
    return {
      success: true,
      nextStage: isPreTask ? undefined : this.getNextStage(),
      output: isPreTask ? undefined : taskOutput,
      checkResults: results,
    };
  }

  private updateStageExecutionStatus(ctx: StageContext, status: StageExecutionStatus): void {
    if (!this.stageExecutionId || !ctx.stageExecutionRepo) return;
    try {
      ctx.stageExecutionRepo.updateStatus(this.stageExecutionId, status);
    } catch (e) {
      log.warn('updateStageExecutionStatus failed', { error: e instanceof Error ? e.message : String(e) });
    }
  }

  private buildCheckContext(ctx: StageContext): CheckContext {
    const changeDir = ctx.artifactManager.getChangeDir(ctx.issue.number);
    return {
      issue: ctx.issue,
      changeDir: changeDir ?? '',
      eventBus: ctx.eventBus,
      projectId: ctx.issue.projectId,
      acpOptions: ctx.acpOptions,
      workflowLogRepo: ctx.workflowLogRepo,
      sessionStreamLogRepo: ctx.sessionStreamLogRepo,
      coderSessionRepo: ctx.coderSessionRepo,
      worktreeManager: ctx.worktreeManager,
      projectRepo: ctx.projectRepo,
    };
  }

  private async dispatchReaction(
    ctx: StageContext,
    check: Check,
    result: CheckResult,
    allResults: CheckResult[],
    taskOutput: unknown,
    taskRetryCount: number,
    isPreTask: boolean,
    activeChecks: Check[],
  ): Promise<StageRunResult> {
    switch (check.reaction.type) {
      case 'retry-task':
        return this.handleRetryTask(ctx, check, result, allResults, taskOutput, taskRetryCount, isPreTask, activeChecks);
      case 'auto-fix':
        return this.handleAutoFix(ctx, check, result, allResults, taskOutput, taskRetryCount, 0, isPreTask, activeChecks);
      case 'escalate':
        return {
          success: false,
          escalateToStage: check.reaction.escalateTarget,
          output: taskOutput,
          checkResults: allResults,
          message: result.message ?? `Check "${check.name}" failed, escalating`,
        };
      case 'ask-user':
        if (result.status === 'fail' && check.reaction.fallbackReaction) {
          return this.dispatchFallbackReaction(ctx, check, result, allResults, taskOutput, taskRetryCount, isPreTask, activeChecks);
        }
        return this.handleAskUser(ctx, check, result, allResults, taskOutput);
    }
  }

  private async handleRetryTask(
    ctx: StageContext,
    check: Check,
    result: CheckResult,
    allResults: CheckResult[],
    taskOutput: unknown,
    taskRetryCount: number,
    isPreTask: boolean,
    activeChecks: Check[],
  ): Promise<StageRunResult> {
    const maxAttempts = check.reaction.maxAttempts ?? 3;

    if (taskRetryCount >= maxAttempts) {
      if (check.reaction.fallbackReaction) {
        return this.dispatchFallbackReaction(ctx, check, result, allResults, taskOutput, taskRetryCount, isPreTask, activeChecks);
      }
      return {
        success: false,
        output: taskOutput,
        checkResults: allResults,
        message: result.message ?? `Check "${check.name}" failed after ${maxAttempts} retries`,
      };
    }

    let newTaskOutput: unknown;
    try {
      newTaskOutput = await this.executeTasks(ctx);
    } catch (err: any) {
      return {
        success: false,
        output: taskOutput,
        checkResults: allResults,
        message: `Task execution failed on retry ${taskRetryCount + 1}: ${err.message}`,
      };
    }

    return this.runChecksPhase(ctx, allResults, activeChecks, newTaskOutput, taskRetryCount + 1, isPreTask);
  }

  private async handleAutoFix(
    ctx: StageContext,
    check: Check,
    result: CheckResult,
    allResults: CheckResult[],
    taskOutput: unknown,
    taskRetryCount: number,
    autoFixAttempt: number,
    isPreTask: boolean,
    activeChecks: Check[],
  ): Promise<StageRunResult> {
    const maxAttempts = check.reaction.maxAttempts ?? 2;

    if (autoFixAttempt >= maxAttempts) {
      if (check.reaction.fallbackReaction) {
        return this.dispatchFallbackReaction(ctx, check, result, allResults, taskOutput, taskRetryCount, isPreTask, activeChecks);
      }
      return {
        success: false,
        output: taskOutput,
        checkResults: allResults,
        message: result.message ?? `Check "${check.name}" failed after ${maxAttempts} auto-fix attempts`,
      };
    }

    const checkCtx = this.buildCheckContext(ctx);

    if ('fix' in check && typeof check.fix === 'function') {
      await check.fix(checkCtx);
    }

    const recheckResult = await check.run(checkCtx);

    if (recheckResult.status === 'pass') {
      if (isPreTask) {
        const continuedResults = [...allResults.slice(0, -1), recheckResult];
        return {
          success: true,
          nextStage: undefined,
          output: undefined,
          checkResults: continuedResults,
        };
      }
      const checks = activeChecks;
      const currentIndex = checks.findIndex(c => c.name === check.name);
      const remaining = checks.slice(currentIndex + 1);
      const continuedResults = [...allResults.slice(0, -1), recheckResult];

      for (const nextCheck of remaining) {
        const nextResult = await nextCheck.run(checkCtx);
        continuedResults.push(nextResult);
        if (nextResult.status !== 'pass') {
          return this.dispatchReaction(ctx, nextCheck, nextResult, continuedResults, taskOutput, taskRetryCount, isPreTask, activeChecks);
        }
      }

      return {
        success: true,
        nextStage: this.getNextStage(),
        output: taskOutput,
        checkResults: continuedResults,
      };
    }

    const updatedResults = [...allResults.slice(0, -1), recheckResult];
    return this.handleAutoFix(ctx, check, recheckResult, updatedResults, taskOutput, taskRetryCount, autoFixAttempt + 1, isPreTask, activeChecks);
  }

  private handleAskUser(
    ctx: StageContext,
    check: Check,
    result: CheckResult,
    allResults: CheckResult[],
    taskOutput: unknown,
  ): StageRunResult {
    let approvalOutput: unknown = null;

    if (ctx.issue.stage === Stage.Plan) {
      const changeDir = ctx.artifactManager.getChangeDir(ctx.issue.number);
      if (changeDir) {
        const selfReviewContent = readReportFile(changeDir, 'self-review.md');
        if (selfReviewContent) {
          const parsedVerdict = parseVerdict(selfReviewContent);
          if (parsedVerdict) {
            const dimensions = parseDimensions(selfReviewContent);
            approvalOutput = {
              result: parsedVerdict,
              selfReviewNotes: selfReviewContent,
              dimensions,
            };
          }
        }
      }
    } else if (ctx.issue.stage === Stage.Check) {
      const aiReviewResult = allResults.find(r => r.name === 'ai-review');
      if (aiReviewResult?.output) {
        const output = aiReviewResult.output as { verdict?: string; reviewReport?: string };
        if (output.verdict && output.reviewReport) {
          const dimensions = parseDimensions(output.reviewReport);
          approvalOutput = {
            result: output.verdict,
            reviewReport: output.reviewReport,
            dimensions,
          };
        }
      }
    }

    ctx.issueRepo.setApprovalState(ctx.issue.id, {
      stage: ctx.issue.stage,
      status: 'awaiting',
      output: approvalOutput,
      requestedAt: new Date().toISOString(),
    });

    ctx.eventBus.emit('approval_requested', {
      issueId: ctx.issue.id,
      projectId: ctx.issue.projectId,
      stage: ctx.issue.stage,
    });

    return {
      success: false,
      output: taskOutput,
      checkResults: allResults,
      message: result.message ?? `Check "${check.name}" requires user approval`,
    };
  }

  private dispatchFallbackReaction(
    ctx: StageContext,
    check: Check,
    result: CheckResult,
    allResults: CheckResult[],
    taskOutput: unknown,
    taskRetryCount: number,
    isPreTask: boolean,
    activeChecks: Check[],
  ): Promise<StageRunResult> {
    const fallback = check.reaction.fallbackReaction!;
    const fallbackCheck: Check = {
      name: check.name,
      reaction: fallback,
      run: async () => result,
    };
    return this.dispatchReaction(ctx, fallbackCheck, result, allResults, taskOutput, taskRetryCount, isPreTask, activeChecks);
  }

  private persistCheckResults(ctx: StageContext, checkResults: CheckResult[]): void {
    if (!this.stageExecutionId || !ctx.stageExecutionRepo) return;
    try {
      ctx.stageExecutionRepo.updateCheckResults(this.stageExecutionId, checkResults);
    } catch (e) {
      log.warn('persistCheckResults failed', { error: e instanceof Error ? e.message : String(e) });
    }
  }
}
