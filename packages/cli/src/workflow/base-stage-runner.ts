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

    if (ctx.workflowRunService && ctx.workflowRun) {
      try {
        ctx.workflowRunService.setStageStarted(ctx.workflowRun.id, ctx.issue.stage);
      } catch (e) {
        log.warn('setStageStarted failed', { error: e instanceof Error ? e.message : String(e) });
      }
    }

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
    }

    this.persistCheckResults(ctx, results);

    const classification = this.classifyPhaseResults(results);

    if (classification.unrepairedFailures.length > 0) {
      const first = classification.unrepairedFailures[0];
      const failedCheck = checks.find(c => c.name === first.name);
      if (failedCheck) {
        return this.handleCheckFailure(ctx, failedCheck, first, results, taskOutput, isPreTask, checks);
      }
    }

    if (classification.repairableFailures.length > 0) {
      const first = classification.repairableFailures[0];
      const failedCheck = checks.find(c => c.name === first.name);
      if (failedCheck) {
        return this.handleCheckFailure(ctx, failedCheck, first, results, taskOutput, isPreTask, checks);
      }
    }

    if (classification.pendingApproval) {
      return this.handleApprovalCheck(ctx, checks.find(c => c.name === 'user-approval')!, classification.pendingApproval, results, taskOutput);
    }

    return {
      success: true,
      nextStage: isPreTask ? undefined : this.getNextStage(),
      output: isPreTask ? undefined : taskOutput,
      checkResults: results,
    };
  }

  private classifyPhaseResults(results: CheckResult[]): {
    repairableFailures: CheckResult[];
    unrepairedFailures: CheckResult[];
    pendingApproval: CheckResult | undefined;
  } {
    const policies = this.getCheckFailurePolicies();
    const repairableFailures: CheckResult[] = [];
    const unrepairedFailures: CheckResult[] = [];
    let pendingApproval: CheckResult | undefined;

    for (const r of results) {
      if (r.status === 'pass') continue;
      if (r.status === 'pending' && this.isApprovalCheck(r.name)) {
        pendingApproval = r;
        continue;
      }
      if (r.status === 'pending') continue;

      const hasPolicy = policies.some(p => p.checkName === r.name);
      if (hasPolicy) {
        repairableFailures.push(r);
      } else {
        unrepairedFailures.push(r);
      }
    }

    return { repairableFailures, unrepairedFailures, pendingApproval };
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

    if (recheckResult.status === 'pass') {
      const continuedResults = [...nextResults];
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

    const updatedResults = [...nextResults];
    this.persistCheckResults(ctx, updatedResults);

    return this.runFixAndRecheck(ctx, check, recheckResult, updatedResults, taskOutput, isPreTask, activeChecks, policy, attempt + 1);
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
      const reviewPassedResult = getLatestCheckResult(allResults, 'review-passed');
      if (!reviewPassedResult?.output) {
        return {
          success: false,
          output: taskOutput,
          checkResults: allResults,
          message: 'Cannot request check approval: no review result found',
        };
      }

      const reviewOutput = reviewPassedResult.output as { verdict?: string; reviewReport?: string };
      if (reviewOutput.verdict !== 'PASS') {
        return {
          success: false,
          output: taskOutput,
          checkResults: allResults,
          message: `Cannot request check approval: latest review verdict is ${reviewOutput.verdict ?? 'unknown'}, expected PASS`,
        };
      }

      const mergeReadyResult = getLatestCheckResult(allResults, 'merge-ready');
      if (!mergeReadyResult || mergeReadyResult.status !== 'pass') {
        return {
          success: false,
          output: taskOutput,
          checkResults: allResults,
          message: mergeReadyResult
            ? `Cannot request check approval: merge-ready is ${mergeReadyResult.status}`
            : 'Cannot request check approval: merge-ready check has not run',
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

      const dimensions = reviewOutput.reviewReport ? parseDimensions(reviewOutput.reviewReport) : undefined;
      const latestReviewPassed = getLatestCheckResult(effectiveResults, 'review-passed');
      const snapshotSha = (latestReviewPassed?.output as { snapshotSha?: string } | undefined)?.snapshotSha;
      approvalOutput = {
        result: reviewOutput.verdict,
        reviewReport: reviewOutput.reviewReport,
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

    if (ctx.workflowRunService && ctx.workflowRun) {
      try {
        ctx.workflowRunService.setApproval(ctx.workflowRun.id, ctx.issue.stage, {
          status: 'awaiting',
          output: approvalOutput,
          requestedAt: new Date().toISOString(),
          respondedAt: null,
        });
      } catch (e) {
        log.warn('workflowRun setApproval failed', { error: e instanceof Error ? e.message : String(e) });
      }
    }

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
    if (ctx.workflowRunService && ctx.workflowRun) {
      try {
        if (status === 'running') {
          ctx.workflowRunService.setStageStarted(ctx.workflowRun.id, ctx.issue.stage);
        } else if (status === 'passed') {
          ctx.workflowRunService.setStagePassed(ctx.workflowRun.id, ctx.issue.stage);
        } else if (status === 'failed') {
          ctx.workflowRunService.setStageFailed(ctx.workflowRun.id, ctx.issue.stage);
        } else if (status === 'awaiting-approval') {
          ctx.workflowRunService.setStageAwaitingApproval(ctx.workflowRun.id, ctx.issue.stage);
        }
      } catch (e) {
        log.warn('workflowRun stage status update failed', { error: e instanceof Error ? e.message : String(e) });
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

    const updatedResults = checkResults.filter(r => r.name !== 'review-passed');
    updatedResults.push(updatedResult);

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

  private convergeSnapshot(ctx: StageContext, checkResults: CheckResult[], snapshotSha: string): CheckResult[] {
    const updated = [...checkResults];
    let latestIdx = -1;
    for (let i = updated.length - 1; i >= 0; i--) {
      if (updated[i].name === 'review-passed') {
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

  private convergeCheckSuite(ctx: StageContext, snapshotSha: string, reviewPassedResult: CheckResult): void {
    if (!ctx.checkSuiteRepo) return;
    try {
      const suite = ctx.checkSuiteRepo.findActiveByIssueId(ctx.issue.id);
      if (!suite) return;

      const output = (reviewPassedResult.output as Record<string, unknown>) ?? {};
      ctx.checkSuiteRepo.updateChecks(suite.id, 'review-passed', {
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
    if (ctx.workflowRunService && ctx.workflowRun) {
      try {
        ctx.workflowRunService.upsertTask(ctx.workflowRun.id, ctx.issue.stage, {
          taskId: result.taskId,
          title: result.title,
          status: result.status === 'completed' ? 'completed' : result.status === 'failed' ? 'failed' : 'skipped',
          attempts: result.attempts,
          duration: result.duration,
          artifacts: result.artifacts,
          output: result.output,
          reason: result.reason ?? null,
          causedByType: result.causedBy?.type ?? null,
          causedByCheckName: result.causedBy?.checkName ?? null,
          causedByTaskId: result.causedBy?.taskId ?? null,
        });
      } catch (e) {
        log.warn('workflowRun upsertTask failed', { error: e instanceof Error ? e.message : String(e) });
      }
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
    if (ctx.workflowRunService && ctx.workflowRun) {
      try {
        for (const [, cr] of seen) {
          ctx.workflowRunService.upsertCheck(ctx.workflowRun.id, ctx.issue.stage, {
            checkName: cr.name,
            title: cr.name,
            status: cr.status === 'pass' ? 'passed' : cr.status === 'fail' ? 'failed' : cr.status === 'error' ? 'error' : 'pending',
            message: cr.message ?? null,
            output: cr.output,
          });
        }
      } catch (e) {
        log.warn('workflowRun upsertCheck failed', { error: e instanceof Error ? e.message : String(e) });
      }
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
