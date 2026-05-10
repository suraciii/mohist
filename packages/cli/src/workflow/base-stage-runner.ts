import { Stage } from '../types';
import type { StageContext, StageRunResult, CheckResult, StageTaskResult, CheckFailurePolicy, AuthoritativeAiReviewResult, AuthoritativeAiReviewOptions } from './stage-context';
import { getLatestCheckResult, buildAuthoritativeAiReviewResult } from './stage-context';
import type { StageRunner } from './check-stage-runner';
import type { Check, CheckContext } from './checks';
import type { StageExecutionStatus } from '../db/stage-execution-repo';
import { normalizeCheckStatus, normalizeTaskStatus, type StageStateStatus } from '../services/stage-state-service';
import { Log } from '../util/log';
import { parseVerdict, parseDimensions, readReportFile } from './utils';
import * as path from 'path';

const log = Log.create({ service: 'base-stage-runner' });

export abstract class BaseStageRunner implements StageRunner {
  abstract canHandle(stage: Stage): boolean;
  protected abstract executeTasks(ctx: StageContext): Promise<unknown>;
  protected abstract getChecks(): Check[];

  protected getPreTaskChecks(): Check[] {
    return [];
  }

  protected abstract getNextStage(): Stage;

  protected getCheckFailurePolicies(): CheckFailurePolicy[] {
    return [];
  }

  protected async runFixTask(
    _ctx: StageContext,
    _taskId: string,
    _failedCheck: CheckResult,
    _attempt: number,
  ): Promise<StageTaskResult | null> {
    return null;
  }

  protected isApprovalCheck(_checkName: string): boolean {
    return false;
  }

  protected async beforeRecheckAfterFix(
    _ctx: StageContext,
    _checkName: string,
    _fixTaskId: string,
  ): Promise<void> {}

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
    this.mirrorTaskResult(ctx, result);
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

    if (ctx.stageStateService) {
      try {
        ctx.stageStateService.ensureStage(ctx.issue.id, ctx.issue.stage);
      } catch (e) {
        log.warn('ensureStage failed', { error: e instanceof Error ? e.message : String(e) });
      }
      try {
        this.mirrorTasksJson(ctx);
      } catch (e) {
        log.warn('mirrorTasksJson failed', { error: e instanceof Error ? e.message : String(e) });
      }
    }

    let checkResults: CheckResult[] = [];
    const preTaskChecks = this.getPreTaskChecks();
    if (preTaskChecks.length > 0) {
      const preTaskResult = await this.runChecksPhase(ctx, [], preTaskChecks, null, true);
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
    const result = await this.runChecksPhase(ctx, checkResults, postTaskChecks, taskOutput, false);
    if (result.success) {
      this.updateStageExecutionStatus(ctx, 'passed');
    } else {
      const hasApproval = result.checkResults?.some(
        (cr: CheckResult) => cr.status === 'pending' && this.isApprovalCheck(cr.name),
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
    isPreTask: boolean,
  ): Promise<StageRunResult> {
    const checkCtx = this.buildCheckContext(ctx);
    const results: CheckResult[] = [...priorResults];

    for (const check of checks) {
      const result = await check.run(checkCtx);
      results.push(result);

      if (check.name === 'ai-review') {
        const authoritativeResults = await this.persistCurrentAiReviewTruth(ctx, results);
        if (authoritativeResults !== results) {
          results.length = 0;
          results.push(...authoritativeResults);
        }
      }

      if (result.status !== 'pass') {
        this.persistCheckResults(ctx, results);
        return this.handleCheckFailure(ctx, check, result, results, taskOutput, isPreTask, checks);
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

  private async handleCheckFailure(
    ctx: StageContext,
    check: Check,
    result: CheckResult,
    allResults: CheckResult[],
    taskOutput: unknown,
    isPreTask: boolean,
    activeChecks: Check[],
  ): Promise<StageRunResult> {
    if (this.isApprovalCheck(check.name)) {
      return this.handleApprovalCheck(ctx, check, result, allResults, taskOutput);
    }

    const policies = this.getCheckFailurePolicies();
    const policy = policies.find(p => p.checkName === check.name);

    if (!policy) {
      return {
        success: false,
        output: taskOutput,
        checkResults: allResults,
        message: result.message ?? `Check "${check.name}" failed`,
      };
    }

    return this.runFixAndRecheck(ctx, check, result, allResults, taskOutput, isPreTask, activeChecks, policy, 0);
  }

  private async runFixAndRecheck(
    ctx: StageContext,
    check: Check,
    failedResult: CheckResult,
    allResults: CheckResult[],
    taskOutput: unknown,
    isPreTask: boolean,
    activeChecks: Check[],
    policy: CheckFailurePolicy,
    attempt: number,
  ): Promise<StageRunResult> {
    if (attempt >= policy.maxAttempts) {
      return {
        success: false,
        output: taskOutput,
        checkResults: allResults,
        message: failedResult.message ?? `Check "${check.name}" failed after ${policy.maxAttempts} fix attempt(s)`,
      };
    }

    const fixResult = await this.runFixTask(ctx, policy.fixTaskId, failedResult, attempt + 1);

    if (fixResult) {
      this.appendTaskResult(ctx, fixResult);
      if (fixResult.status !== 'completed') {
        return {
          success: false,
          output: taskOutput,
          checkResults: allResults,
          message: `${fixResult.title} failed`,
        };
      }
    }

    await this.beforeRecheckAfterFix(ctx, check.name, policy.fixTaskId);

    const checkCtx = this.buildCheckContext(ctx);
    const recheckResult = await check.run(checkCtx);

    const nextResults = [...allResults, recheckResult];
    const authoritativeResults = check.name === 'ai-review'
      ? await this.persistCurrentAiReviewTruth(ctx, nextResults)
      : nextResults;

    if (recheckResult.status === 'pass') {
      const continuedResults = [...authoritativeResults];
      this.persistCheckResults(ctx, continuedResults);

      if (isPreTask) {
        return {
          success: true,
          nextStage: undefined,
          output: undefined,
          checkResults: continuedResults,
        };
      }

      const currentIndex = activeChecks.findIndex(c => c.name === check.name);
      const remaining = activeChecks.slice(currentIndex + 1);

      for (const nextCheck of remaining) {
        const nextResult = await nextCheck.run(checkCtx);
        continuedResults.push(nextResult);
        if (nextResult.status !== 'pass') {
          this.persistCheckResults(ctx, continuedResults);
          return this.handleCheckFailure(ctx, nextCheck, nextResult, continuedResults, taskOutput, isPreTask, activeChecks);
        }
      }

      return {
        success: true,
        nextStage: this.getNextStage(),
        output: taskOutput,
        checkResults: continuedResults,
      };
    }

    const updatedResults = [...authoritativeResults];
    this.persistCheckResults(ctx, updatedResults);

    return this.runFixAndRecheck(ctx, check, recheckResult, updatedResults, taskOutput, isPreTask, activeChecks, policy, attempt + 1);
  }

  private async persistCurrentAiReviewTruth(ctx: StageContext, checkResults: CheckResult[]): Promise<CheckResult[]> {
    const latest = getLatestCheckResult(checkResults, 'ai-review');
    if (!latest) return checkResults;

    const project = this.findProjectSafe(ctx);
    const worktreePath = project
      ? ctx.worktreeManager.getPath(project.name, ctx.issue.number)
      : null;
    const snapshotSha = worktreePath ? await this.getHeadShaSafe(ctx, worktreePath) : undefined;
    const changeDir = ctx.artifactManager.getChangeDir(ctx.issue.number);

    return this.persistAuthoritativeAiReview(ctx, checkResults, {
      snapshotSha,
      reviewArtifactPath: changeDir ? path.join(changeDir, 'review.md') : undefined,
      selfCheckArtifactPath: changeDir ? path.join(changeDir, 'review-self-check.md') : undefined,
    });
  }

  private async getHeadShaSafe(ctx: StageContext, worktreePath: string): Promise<string | undefined> {
    if (typeof ctx.worktreeManager.getHeadSha !== 'function') {
      return undefined;
    }

    try {
      return await ctx.worktreeManager.getHeadSha(worktreePath);
    } catch (e) {
      log.warn('getHeadSha failed for authoritative ai-review persistence', {
        error: e instanceof Error ? e.message : String(e),
      });
      return undefined;
    }
  }

  private findProjectSafe(ctx: StageContext) {
    const projectRepo = ctx.projectRepo;
    return projectRepo && typeof projectRepo.findById === 'function'
      ? projectRepo.findById(ctx.issue.projectId)
      : null;
  }

  private async handleApprovalCheck(
    ctx: StageContext,
    _check: Check,
    result: CheckResult,
    allResults: CheckResult[],
    taskOutput: unknown,
  ): Promise<StageRunResult> {
    let approvalOutput: unknown = null;
    let effectiveResults = allResults;

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
      const aiReviewResult = getLatestCheckResult(allResults, 'ai-review');
      if (!aiReviewResult?.output) {
        return {
          success: false,
          output: taskOutput,
          checkResults: allResults,
          message: 'Cannot request check approval: no AI review result found',
        };
      }

      const aiReviewOutput = aiReviewResult.output as { verdict?: string; reviewReport?: string };
      if (aiReviewOutput.verdict !== 'PASS') {
        return {
          success: false,
          output: taskOutput,
          checkResults: allResults,
          message: `Cannot request check approval: latest AI review verdict is ${aiReviewOutput.verdict ?? 'unknown'}, expected PASS`,
        };
      }

      const project = this.findProjectSafe(ctx);
      const worktreePath = project
        ? ctx.worktreeManager.getPath(project.name, ctx.issue.number)
        : null;

      if (worktreePath && typeof ctx.worktreeManager.createCheckConvergenceCommit === 'function') {
        const convergence = await ctx.worktreeManager.createCheckConvergenceCommit(worktreePath, ctx.issue.number);
        if (!convergence.success) {
          return {
            success: false,
            output: taskOutput,
            checkResults: allResults,
            message: convergence.error ?? 'Cannot request check approval: uncommitted auto-fix or review artifact changes prevented approval',
          };
        }

        effectiveResults = this.convergeSnapshot(ctx, allResults, convergence.headSha);
      }

      const dimensions = aiReviewOutput.reviewReport ? parseDimensions(aiReviewOutput.reviewReport) : undefined;
      const latestAiReview = getLatestCheckResult(effectiveResults, 'ai-review');
      const snapshotSha = (latestAiReview?.output as { snapshotSha?: string } | undefined)?.snapshotSha;
      approvalOutput = {
        result: aiReviewOutput.verdict,
        reviewReport: aiReviewOutput.reviewReport,
        dimensions,
        snapshotSha,
      };
    }

    ctx.issueRepo.setApprovalState(ctx.issue.id, {
      stage: ctx.issue.stage,
      status: 'awaiting',
      output: approvalOutput,
      requestedAt: new Date().toISOString(),
    });

    if (ctx.stageStateService) {
      try {
        ctx.stageStateService.setApproval(ctx.issue.id, ctx.issue.stage, {
          status: 'awaiting',
          output: approvalOutput,
          requestedAt: new Date().toISOString(),
        });
      } catch (e) {
        log.warn('setApproval failed', { error: e instanceof Error ? e.message : String(e) });
      }
    }

    ctx.eventBus.emit('approval_requested', {
      issueId: ctx.issue.id,
      projectId: ctx.issue.projectId,
      stage: ctx.issue.stage,
    });

    return {
      success: false,
      output: taskOutput,
      checkResults: effectiveResults,
      message: result.message ?? `User approval required`,
    };
  }

  private updateStageExecutionStatus(ctx: StageContext, status: StageExecutionStatus): void {
    if (!this.stageExecutionId || !ctx.stageExecutionRepo) return;
    try {
      ctx.stageExecutionRepo.updateStatus(this.stageExecutionId, status);
    } catch (e) {
      log.warn('updateStageExecutionStatus failed', { error: e instanceof Error ? e.message : String(e) });
    }
    if (ctx.stageStateService) {
      try {
        ctx.stageStateService.setStageStatus(ctx.issue.id, ctx.issue.stage, status as StageStateStatus);
      } catch (e) {
        log.warn('setStageStatus failed', { error: e instanceof Error ? e.message : String(e) });
      }
    }
  }

  protected persistAuthoritativeAiReview(
    ctx: StageContext,
    checkResults: CheckResult[],
    options?: AuthoritativeAiReviewOptions,
  ): CheckResult[] {
    const latest = getLatestCheckResult(checkResults, 'ai-review');
    if (!latest) return checkResults;

    const authoritative = buildAuthoritativeAiReviewResult(latest, options);
    if (!authoritative) return checkResults;

    const updatedOutput = {
      ...((latest.output as Record<string, unknown>) ?? {}),
      snapshotSha: authoritative.snapshotSha,
      reviewArtifactPath: authoritative.reviewArtifactPath,
      selfCheckArtifactPath: authoritative.selfCheckArtifactPath,
      convergedAt: authoritative.convergedAt,
    };
    const updatedResult: CheckResult = {
      ...latest,
      output: updatedOutput,
    };

    const updatedResults = checkResults.filter(r => r.name !== 'ai-review');
    updatedResults.push(updatedResult);

    this.persistCheckResults(ctx, updatedResults);
    this.updateCheckSuiteAiReview(ctx, authoritative);

    return updatedResults;
  }

  private updateCheckSuiteAiReview(ctx: StageContext, result: AuthoritativeAiReviewResult): void {
    if (!ctx.checkSuiteRepo) return;
    try {
      const suite = ctx.checkSuiteRepo.findActiveByIssueId(ctx.issue.id);
      if (!suite) return;

      const status = result.verdict === 'PASS' ? 'passed' : 'failed';
      ctx.checkSuiteRepo.updateChecks(suite.id, 'ai-review', {
        status,
        output: result,
        ranAt: result.convergedAt,
      });

      if (result.snapshotSha && result.snapshotSha !== suite.snapshotSha) {
        ctx.checkSuiteRepo.updateSnapshotSha(suite.id, result.snapshotSha);
      }
    } catch (e) {
      log.warn('updateCheckSuiteAiReview failed', { error: e instanceof Error ? e.message : String(e) });
    }
  }

  private convergeSnapshot(ctx: StageContext, checkResults: CheckResult[], snapshotSha: string): CheckResult[] {
    const updated = [...checkResults];
    let latestIdx = -1;
    for (let i = updated.length - 1; i >= 0; i--) {
      if (updated[i].name === 'ai-review') {
        latestIdx = i;
        break;
      }
    }

    if (latestIdx >= 0) {
      const existing = updated[latestIdx];
      updated[latestIdx] = {
        ...existing,
        output: {
          ...((existing.output as Record<string, unknown>) ?? {}),
          snapshotSha,
          convergedAt: new Date().toISOString(),
        },
      };
    }

    this.persistCheckResults(ctx, updated);

    if (latestIdx >= 0) {
      this.convergeCheckSuite(ctx, snapshotSha, updated[latestIdx]);
    }

    return updated;
  }

  private convergeCheckSuite(ctx: StageContext, snapshotSha: string, aiReviewResult: CheckResult): void {
    if (!ctx.checkSuiteRepo) return;
    try {
      const suite = ctx.checkSuiteRepo.findActiveByIssueId(ctx.issue.id);
      if (!suite) return;

      const output = (aiReviewResult.output as Record<string, unknown>) ?? {};
      ctx.checkSuiteRepo.updateChecks(suite.id, 'ai-review', {
        status: 'passed',
        output,
        ranAt: (output.convergedAt as string) ?? new Date().toISOString(),
      });

      ctx.checkSuiteRepo.updateSnapshotShaPreservingChecks(suite.id, snapshotSha);
    } catch (e) {
      log.warn('convergeCheckSuite failed', { error: e instanceof Error ? e.message : String(e) });
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

  private persistCheckResults(ctx: StageContext, checkResults: CheckResult[]): void {
    if (!this.stageExecutionId || !ctx.stageExecutionRepo) return;
    try {
      ctx.stageExecutionRepo.updateCheckResults(this.stageExecutionId, checkResults);
    } catch (e) {
      log.warn('persistCheckResults failed', { error: e instanceof Error ? e.message : String(e) });
    }
    this.mirrorCheckResults(ctx, checkResults);
  }

  private mirrorTaskResult(ctx: StageContext, result: StageTaskResult): void {
    if (!ctx.stageStateService) return;
    try {
      ctx.stageStateService.upsertTask(ctx.issue.id, ctx.issue.stage, {
        taskId: result.taskId,
        title: result.title,
        status: normalizeTaskStatus(result.status),
        source: 'dynamic',
        attempts: result.attempts,
        duration: result.duration,
        artifacts: result.artifacts,
        output: result.output,
      });
    } catch (e) {
      log.warn('mirrorTaskResult failed', { error: e instanceof Error ? e.message : String(e) });
    }
  }

  private mirrorCheckResults(ctx: StageContext, checkResults: CheckResult[]): void {
    if (!ctx.stageStateService) return;
    const seen = new Map<string, CheckResult>();
    for (const cr of checkResults) {
      seen.set(cr.name, cr);
    }
    for (const [, cr] of seen) {
      try {
        ctx.stageStateService.upsertCheck(ctx.issue.id, ctx.issue.stage, {
          checkName: cr.name,
          status: normalizeCheckStatus(cr.status),
          message: cr.message ?? null,
          output: cr.output,
        });
      } catch (e) {
        log.warn('mirrorCheckResult failed', { checkName: cr.name, error: e instanceof Error ? e.message : String(e) });
      }
    }
  }

  private mirrorTasksJson(ctx: StageContext): void {
    if (!ctx.stageStateService) return;
    const tasksFile = ctx.artifactManager.readTasks(ctx.issue.number);
    if (!tasksFile) return;
    for (const t of tasksFile.tasks) {
      try {
        ctx.stageStateService.upsertTask(ctx.issue.id, ctx.issue.stage, {
          taskId: t.id,
          title: t.title,
          status: t.passes ? 'completed' : 'pending',
          source: 'dynamic',
          order: t.order,
          attempts: t.attempts,
        });
      } catch (e) {
        log.warn('mirrorTasksJson task failed', { taskId: t.id, error: e instanceof Error ? e.message : String(e) });
      }
    }
  }
}
