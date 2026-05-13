import { Stage } from '../types';
import type { StageContext, StageRunResult, CheckResult, StageTaskResult, CheckFailurePolicy, AuthoritativeAiReviewResult, AuthoritativeAiReviewOptions } from './stage-context';
import { getLatestCheckResult, buildAuthoritativeAiReviewResult } from './stage-context';
import type { StageRunner } from './check-stage-runner';
import type { Check, CheckContext } from './checks';
import type { StageExecutionStatus } from '../db/stage-execution-repo';
import { normalizeCheckStatus, normalizeTaskStatus, type StageStateStatus } from '../services/stage-state-service';
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

  protected getCheckFailurePolicies(): CheckFailurePolicy[] {
    return [];
  }

  protected async runFixTask(
    ctx: StageContext,
    taskId: string,
    failedCheck: CheckResult,
    attempt: number,
  ): Promise<StageTaskResult | null> {
    return this.executeReportedTask(ctx, taskId, failedCheck, attempt);
  }

  protected async beforeRecheckAfterFix(
    _ctx: StageContext,
    _checkName: string,
    _fixTaskId: string,
  ): Promise<void> {
    // Optional legacy hook for runners that must invalidate stale artifacts before rechecking.
  }

  async executeTaskWork(ctx: StageContext, taskId: string, options: { failedCheck?: CheckResult; attempt?: number } = {}): Promise<StageTaskResult | null> {
    const result = await this.executeReportedTask(ctx, taskId, options.failedCheck, options.attempt ?? 1);
    if (result && !result.alreadyReported) this.appendTaskResult(ctx, result);
    return result;
  }

  async executeCheckWork(ctx: StageContext, checkName: string): Promise<CheckResult> {
    if (ctx.workflowApplicationService && this.isApprovalCheck(checkName)) {
      const output = this.buildApprovalOutput(ctx, []);
      if (output && typeof output === 'object' && 'error' in output) {
        return { name: checkName, status: 'fail', message: String((output as { error: unknown }).error) };
      }
      ctx.workflowApplicationService.approveStage({
        issueId: ctx.issue.id,
        stage: ctx.issue.stage,
        approval: { output },
      });
      return { name: checkName, status: 'pass', output };
    }

    const check = [...this.getPreTaskChecks(), ...this.getChecks()].find(candidate => candidate.name === checkName);
    if (!check) throw new Error(`Check ${checkName} is not registered for stage ${ctx.issue.stage}`);

    const result = await check.run(this.buildCheckContext(ctx));
    this.persistCheckResults(ctx, [result]);
    return result;
  }

  protected failedCheckForRequestedTask(ctx: StageContext): CheckResult | undefined {
    const checkName = ctx.requestedTask?.causedBy?.checkName;
    if (!checkName) return undefined;
    const stageRun = ctx.workflowRun?.stageRuns.find(candidate => candidate.stage === ctx.issue.stage);
    const check = stageRun?.checks.find(candidate => candidate.checkName === checkName);
    if (!check) return undefined;
    const status = check.status === 'passed'
      ? 'pass'
      : check.status === 'pending' || check.status === 'running'
        ? 'pending'
        : check.status === 'error'
          ? 'error'
          : 'fail';
    return {
      name: check.checkName,
      status,
      message: check.message ?? ctx.requestedTask?.reason ?? ctx.requestedTask?.causedBy?.message,
      output: check.output ?? undefined,
    };
  }

  protected async executeReportedTask(
    _ctx: StageContext,
    _taskId: string,
    _failedCheck: CheckResult | undefined,
    _attempt: number,
  ): Promise<StageTaskResult | null> {
    return null;
  }

  protected isApprovalCheck(_checkName: string): boolean {
    return false;
  }

  private stageExecutionId?: string;

  protected getStageExecutionId(): string | undefined {
    return this.stageExecutionId;
  }

  protected appendTaskResult(ctx: StageContext, result: StageTaskResult): void {
    if (this.stageExecutionId && ctx.stageExecutionRepo) {
      try {
        ctx.stageExecutionRepo.appendTaskResult(this.stageExecutionId, result);
      } catch (e) {
        log.warn('appendTaskResult failed', { error: e instanceof Error ? e.message : String(e) });
      }
    }
    this.mirrorTaskResult(ctx, result);
    this.reportTaskResult(ctx, result);
  }

  async run(ctx: StageContext): Promise<StageRunResult> {
    if (ctx.requestedWork?.kind === 'task') {
      const result = await this.executeTaskWork(ctx, ctx.requestedWork.taskId, {
        failedCheck: this.failedCheckForRequestedTask(ctx),
        attempt: (ctx.requestedTask?.attempts ?? 0) + 1,
      });
      if (!result) {
        return {
          success: false,
          output: null,
          checkResults: [],
          message: `Task ${ctx.requestedWork.taskId} is not executable by ${this.constructor.name}`,
        };
      }
      return {
        success: result?.status !== 'failed',
        output: result?.output ?? null,
        checkResults: [],
        message: result?.status === 'failed' ? result.reason ?? `Task ${ctx.requestedWork.taskId} failed` : undefined,
      };
    }

    if (ctx.requestedWork?.kind === 'check') {
      const result = await this.executeCheckWork(ctx, ctx.requestedWork.checkName);
      return {
        success: result.status === 'pass' || result.status === 'pending',
        output: result.output ?? null,
        checkResults: [result],
        message: result.status === 'pass' || result.status === 'pending'
          ? undefined
          : result.message ?? `Check "${result.name}" ${result.status}`,
      };
    }

    this.stageExecutionId = undefined;
    this.reportedCheckResultCount = 0;

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
    }

    this.persistCheckResults(ctx, results);

    const firstBlockingResult = results.find(r => r.status !== 'pass');
    if (firstBlockingResult) {
      if (firstBlockingResult.status === 'pending' && this.isApprovalCheck(firstBlockingResult.name)) {
        if (!ctx.workflowApplicationService) {
          const approvalResults = await this.convergeReviewForApproval(ctx, results);
          if (!approvalResults.ok) {
            return {
              success: false,
              output: taskOutput,
              checkResults: results,
              message: approvalResults.message,
            };
          }
          const approval = await this.prepareApproval(ctx, approvalResults.results);
          if (!approval.ok) {
            return {
              success: false,
              output: taskOutput,
              checkResults: approvalResults.results,
              message: approval.message,
            };
          }
          this.requestApproval(ctx, approval.output);
          return {
            success: false,
            output: taskOutput,
            checkResults: approvalResults.results,
            message: firstBlockingResult.message ?? `Check "${firstBlockingResult.name}" ${firstBlockingResult.status}`,
          };
        }
      }

      if (!ctx.workflowApplicationService && firstBlockingResult.status !== 'pending') {
        const repaired = await this.tryLegacyRepair(ctx, priorResults, checks, taskOutput, firstBlockingResult);
        if (repaired) return repaired;
      }

      return {
        success: false,
        output: taskOutput,
        checkResults: results,
        message: firstBlockingResult.message ?? `Check "${firstBlockingResult.name}" ${firstBlockingResult.status}`,
      };
    }

    return {
      success: true,
      output: isPreTask ? undefined : taskOutput,
      checkResults: results,
    };
  }

  private async tryLegacyRepair(
    ctx: StageContext,
    priorResults: CheckResult[],
    checks: Check[],
    taskOutput: unknown,
    failedCheck: CheckResult,
  ): Promise<StageRunResult | null> {
    const policy = this.getCheckFailurePolicies().find(candidate => candidate.checkName === failedCheck.name);
    if (!policy) return null;

    const accumulatedResults = [...priorResults, failedCheck];
    let lastResults: CheckResult[] = [];
    for (let attempt = 1; attempt <= policy.maxAttempts; attempt++) {
      const fixResult = await this.runFixTask(ctx, policy.fixTaskId, failedCheck, attempt);
      if (fixResult) this.appendTaskResult(ctx, fixResult);
      if (fixResult?.status === 'failed') {
        return {
          success: false,
          output: taskOutput,
          checkResults: accumulatedResults,
          message: fixResult?.reason ?? `${fixResult?.title ?? policy.fixTaskId} failed`,
        };
      }

      await this.beforeRecheckAfterFix(ctx, failedCheck.name, policy.fixTaskId);

      lastResults = [...priorResults];
      for (const check of checks) {
        lastResults.push(await check.run(this.buildCheckContext(ctx)));
      }
      accumulatedResults.push(...lastResults.slice(priorResults.length));
      this.persistCheckResults(ctx, accumulatedResults);

      const nextBlocking = lastResults.find(r => r.status !== 'pass');
      if (!nextBlocking) {
        return {
          success: true,
          output: taskOutput,
          checkResults: accumulatedResults,
        };
      }
      if (nextBlocking.name !== failedCheck.name || nextBlocking.status === 'pending') {
        if (nextBlocking.status === 'pending' && this.isApprovalCheck(nextBlocking.name)) {
          const approvalResults = await this.convergeReviewForApproval(ctx, accumulatedResults);
          if (!approvalResults.ok) {
            return {
              success: false,
              output: taskOutput,
              checkResults: accumulatedResults,
              message: approvalResults.message,
            };
          }
          const approval = await this.prepareApproval(ctx, approvalResults.results);
          if (!approval.ok) {
            return {
              success: false,
              output: taskOutput,
              checkResults: approvalResults.results,
              message: approval.message,
            };
          }
          this.requestApproval(ctx, approval.output);
          return {
            success: false,
            output: taskOutput,
            checkResults: approvalResults.results,
            message: nextBlocking.message ?? `Check "${nextBlocking.name}" ${nextBlocking.status}`,
          };
        }
        return {
          success: false,
          output: taskOutput,
          checkResults: accumulatedResults,
          message: nextBlocking.message ?? `Check "${nextBlocking.name}" ${nextBlocking.status}`,
        };
      }
    }

    const finalBlocking = lastResults.find(r => r.status !== 'pass') ?? failedCheck;
    return {
      success: false,
      output: taskOutput,
      checkResults: accumulatedResults,
      message: finalBlocking.message ?? `Check "${finalBlocking.name}" ${finalBlocking.status}`,
    };
  }

  private async convergeReviewForApproval(
    ctx: StageContext,
    checkResults: CheckResult[],
  ): Promise<{ ok: true; results: CheckResult[] } | { ok: false; message: string }> {
    if (ctx.issue.stage !== Stage.Check) return { ok: true, results: checkResults };

    const latestReviewPassed = getLatestCheckResult(checkResults, 'review-passed');
    const reviewOutput = latestReviewPassed?.output as { verdict?: string; snapshotSha?: string } | undefined;
    if (reviewOutput?.verdict !== 'PASS' || reviewOutput.snapshotSha) {
      return { ok: true, results: checkResults };
    }

    try {
      const project = ctx.projectRepo?.findById(ctx.issue.projectId);
      const worktreePath = project
        ? ctx.worktreeManager.getPath(project.name, ctx.issue.number)
        : null;
      if (!worktreePath) {
        return { ok: false, message: 'Cannot request check approval: worktree not found for review convergence' };
      }

      const convergence = await ctx.worktreeManager.createCheckConvergenceCommit(worktreePath, ctx.issue.number);
      if (!convergence.success) {
        return { ok: false, message: convergence.error ?? 'Convergence commit failed' };
      }

      return {
        ok: true,
        results: this.persistAuthoritativeReview(ctx, checkResults, { snapshotSha: convergence.headSha }),
      };
    } catch (err) {
      return {
        ok: false,
        message: err instanceof Error ? err.message : String(err),
      };
    }
  }

  private async prepareApproval(ctx: StageContext, allResults: CheckResult[]): Promise<{ ok: true; output: unknown } | { ok: false; message: string }> {
    if (ctx.issue.stage === Stage.Check) {
      const latestReviewPassed = getLatestCheckResult(allResults, 'review-passed');
      if (!latestReviewPassed) return { ok: false, message: 'Cannot request check approval: no review result found' };
      const reviewOutput = latestReviewPassed.output as { verdict?: string; snapshotSha?: string } | undefined;
      if (reviewOutput?.verdict === 'PASS' && !reviewOutput.snapshotSha) {
        return { ok: false, message: 'Cannot request check approval: review snapshot has not been converged' };
      }
    }

    const output = this.buildApprovalOutput(ctx, allResults);
    if (output && typeof output === 'object' && 'error' in output) {
      return { ok: false, message: String((output as { error: unknown }).error) };
    }
    return { ok: true, output };
  }

  private requestApproval(ctx: StageContext, output: unknown): void {
    const requestedAt = new Date().toISOString();
    try {
      ctx.issueRepo.setApprovalState(ctx.issue.id, {
        stage: ctx.issue.stage,
        status: 'awaiting',
        output,
        requestedAt,
      });
    } catch (e) {
      log.warn('setApprovalState failed', { error: e instanceof Error ? e.message : String(e) });
    }
    if (ctx.stageStateService) {
      try {
        ctx.stageStateService.setApproval(ctx.issue.id, ctx.issue.stage, {
          status: 'awaiting',
          output,
          requestedAt,
        });
      } catch (e) {
        log.warn('stageStateService.setApproval failed', { error: e instanceof Error ? e.message : String(e) });
      }
    }
    try {
      ctx.eventBus.emit('approval_requested', {
        issueId: ctx.issue.id,
        projectId: ctx.issue.projectId,
        stage: ctx.issue.stage,
      });
    } catch (e) {
      log.warn('approval_requested emit failed', { error: e instanceof Error ? e.message : String(e) });
    }
  }

  protected buildApprovalOutput(ctx: StageContext, allResults: CheckResult[]): unknown {
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
      const reviewPassedResult = getLatestCheckResult(allResults, 'review-passed');
      if (!reviewPassedResult?.output) {
        return { error: 'Cannot request check approval: no review result found' };
      }

      const reviewOutput = reviewPassedResult.output as { verdict?: string; reviewReport?: string };
      if (reviewOutput.verdict !== 'PASS') {
        return { error: `Cannot request check approval: latest review verdict is ${reviewOutput.verdict ?? 'unknown'}, expected PASS` };
      }

      const mergeReadyResult = getLatestCheckResult(allResults, 'merge-ready');
      if (!mergeReadyResult || mergeReadyResult.status !== 'pass') {
        return {
          error: mergeReadyResult
            ? `Cannot request check approval: merge-ready is ${mergeReadyResult.status}`
            : 'Cannot request check approval: merge-ready check has not run',
        };
      }

      const dimensions = reviewOutput.reviewReport ? parseDimensions(reviewOutput.reviewReport) : undefined;
      const latestReviewPassed = getLatestCheckResult(allResults, 'review-passed');
      const snapshotSha = (latestReviewPassed?.output as { snapshotSha?: string } | undefined)?.snapshotSha;
      approvalOutput = {
        result: reviewOutput.verdict,
        reviewReport: reviewOutput.reviewReport,
        dimensions,
        snapshotSha,
      };
    }

    return approvalOutput;
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

  protected persistAuthoritativeReview(
    ctx: StageContext,
    checkResults: CheckResult[],
    options?: AuthoritativeAiReviewOptions,
  ): CheckResult[] {
    const latest = getLatestCheckResult(checkResults, 'review-passed');
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

    const updatedResults = [...checkResults];
    for (let i = updatedResults.length - 1; i >= 0; i--) {
      if (updatedResults[i].name === 'review-passed') {
        updatedResults[i] = updatedResult;
        break;
      }
    }

    this.persistCheckResults(ctx, updatedResults);
    this.updateCheckSuiteReviewPassed(ctx, authoritative);

    return updatedResults;
  }

  private updateCheckSuiteReviewPassed(ctx: StageContext, result: AuthoritativeAiReviewResult): void {
    if (!ctx.checkSuiteRepo) return;
    try {
      const suite = ctx.checkSuiteRepo.findActiveByIssueId(ctx.issue.id);
      if (!suite) return;

      const status = result.verdict === 'PASS' ? 'passed' : 'failed';
      ctx.checkSuiteRepo.updateChecks(suite.id, 'review-passed', {
        status,
        output: result,
        ranAt: result.convergedAt,
      });

      if (result.snapshotSha && result.snapshotSha !== suite.snapshotSha) {
        ctx.checkSuiteRepo.updateSnapshotSha(suite.id, result.snapshotSha);
      }
    } catch (e) {
      log.warn('updateCheckSuiteReviewPassed failed', { error: e instanceof Error ? e.message : String(e) });
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
    if (this.stageExecutionId && ctx.stageExecutionRepo) {
      try {
        ctx.stageExecutionRepo.updateCheckResults(this.stageExecutionId, checkResults);
      } catch (e) {
        log.warn('persistCheckResults failed', { error: e instanceof Error ? e.message : String(e) });
      }
    }
    this.mirrorCheckResults(ctx, checkResults);
    this.reportNewCheckResults(ctx, checkResults);
  }

  private mirrorTaskResult(ctx: StageContext, result: StageTaskResult): void {
    if (ctx.workflowApplicationService) return;
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
    if (ctx.workflowApplicationService) return;
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

  private reportTaskResult(ctx: StageContext, result: StageTaskResult): void {
    if (!ctx.workflowApplicationService) return;
    ctx.workflowApplicationService.completeTask({
      issueId: ctx.issue.id,
      stage: ctx.issue.stage,
      taskId: result.taskId,
      result: {
        status: result.status,
        attempts: result.attempts,
        duration: result.duration,
        artifacts: result.artifacts,
        output: result.output,
        reason: result.reason,
        causedBy: result.causedBy,
      },
    });
  }

  private reportedCheckResultCount = 0;

  private reportNewCheckResults(ctx: StageContext, checkResults: CheckResult[]): void {
    if (!ctx.workflowApplicationService) return;
    const pending = ctx.requestedWork?.kind === 'check'
      ? checkResults
      : checkResults.slice(this.reportedCheckResultCount);
    this.reportedCheckResultCount = ctx.requestedWork?.kind === 'check'
      ? 0
      : checkResults.length;
    for (const result of pending) {
      const output = this.isApprovalCheck(result.name) && result.status === 'pending'
        ? this.buildApprovalOutput(ctx, checkResults)
        : result.output;
      ctx.workflowApplicationService.recordCheckResult({
        issueId: ctx.issue.id,
        stage: ctx.issue.stage,
        result: {
          name: result.name,
          status: result.status,
          message: result.message,
          output,
        },
      });
    }
  }

  private mirrorTasksJson(ctx: StageContext): void {
    if (!ctx.stageStateService) return;
    const tasksFile = ctx.artifactManager.readTasks(ctx.issue.number);
    if (!tasksFile) return;
    for (const t of tasksFile.tasks) {
      try {
        const status = t.passes
          ? 'completed'
          : t.error
            ? 'failed'
            : 'pending';
        ctx.stageStateService.upsertTask(ctx.issue.id, ctx.issue.stage, {
          taskId: t.id,
          title: t.title,
          status,
          source: 'dynamic',
          order: t.order,
          attempts: t.attempts,
          output: t.error ? { error: t.error } : undefined,
        });
      } catch (e) {
        log.warn('mirrorTasksJson task failed', { taskId: t.id, error: e instanceof Error ? e.message : String(e) });
      }
    }
  }
}
