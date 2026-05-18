import { Stage } from '../types';
import type { CheckFailurePolicy, StageContext, StageRunResult, StageTaskResult } from './stage-context';
import { buildAuthoritativeAiReviewResult, emitStageTaskUpdate } from './stage-context';
import { BaseStageRunner } from './base-stage-runner';
import type { Check } from './checks';
import { ReviewPassedCheck } from './checks/review-passed-check';
import { MergeReadyCheck } from './checks/merge-ready-check';
import { UserApprovalCheck } from './checks/user-approval-check';
import { HealthGateCheck } from './checks/health-gate-check';
import { buildReviewerPrompt } from '../agents/artifact-prompt';
import { AgentSession, type AgentSessionOptions } from '../agent-runtime/agent-session';
import { validateReviewArtifact } from './utils';
import { Log } from '../util/log';
import { createWorkflowSessionObservers } from '../agent-runtime';
import { createRepairFixAdapter } from './task-runtime/repair-fix-adapter';
import { executeRebaseBranchTask } from './task-runtime/rebase-task-handler';
import { loadHealthGatePolicies, loadWorkflow } from './workflow-loader';
import * as fs from 'node:fs';

const log = Log.create({ service: 'check-stage-runner' });

export interface StageRunner {
  canHandle(stage: Stage): boolean;
  materializeWork?(ctx: StageContext): Promise<boolean> | boolean;
  run(ctx: StageContext): Promise<StageRunResult>;
}

export interface CheckStageRunnerOptions {
  worktreePath: string;
  checks?: Check[];
  healthGatePolicy?: import('./workflow-loader').HealthGatePolicy;
}

export class CheckStageRunner extends BaseStageRunner implements StageRunner {
  private postTaskChecks: Check[];
  private usesDefaultChecks: boolean;
  private worktreePath: string;
  private checkHealthGatePolicy: import('./workflow-loader').HealthGatePolicy;

  constructor(options: CheckStageRunnerOptions) {
    super();
    this.worktreePath = options.worktreePath;
    this.usesDefaultChecks = !options.checks;
    this.checkHealthGatePolicy = options.healthGatePolicy ?? {
      enabled: true,
      command: 'npm run build && npm test',
      timeout: 300000,
      autoFix: false,
      maxFixAttempts: 0,
      fallbackReaction: { type: 'ask-user' },
    };
    this.postTaskChecks = options.checks ?? [
      new ReviewPassedCheck(),
      new MergeReadyCheck(),
      new UserApprovalCheck(Stage.Check),
    ];
  }

  canHandle(stage: Stage): boolean {
    return stage === Stage.Check;
  }

  protected getPreTaskChecks(): Check[] {
    if (!this.usesDefaultChecks) return [];
    const wf = loadWorkflow(this.worktreePath);
    const policy = typeof wf === 'string'
      ? this.checkHealthGatePolicy
      : loadHealthGatePolicies(wf).check;
    return [
      new HealthGateCheck({
        worktreePath: this.worktreePath,
        policy,
        stage: 'check',
      }),
    ];
  }

  protected getCheckFailurePolicies(_ctx?: StageContext): CheckFailurePolicy[] {
    return [];
  }

  protected async runFixTask(
    ctx: StageContext,
    _taskId: string,
    failedCheck: import('./stage-context').CheckResult,
    attempt: number,
  ): Promise<import('./stage-context').StageTaskResult | null> {
    const adapter = createRepairFixAdapter();
    const project = ctx.projectRepo?.findById(ctx.issue.projectId);
    const worktreePath = project
      ? ctx.worktreeManager.getPath(project.name, ctx.issue.number)
      : null;

    if (failedCheck.name === 'review-passed') {
      if (!worktreePath) {
        log.warn('runFixTask: no worktree path found', { issueNumber: ctx.issue.number });
        return null;
      }
      const result = await adapter.dispatch('fix-review-findings', ctx, {
        worktreePath,
        failedCheck,
        attempt,
      });
      if (result.status === 'completed') {
        this.invalidateReviewArtifactForRereview(ctx);
      }
      return result;
    }

    if (failedCheck.name === 'merge-ready') {
      if (!worktreePath) {
        log.warn('runFixTask: no worktree path found', { issueNumber: ctx.issue.number });
        return null;
      }
      return adapter.dispatch('repair-merge', ctx, {
        worktreePath,
        failedCheck,
        attempt,
      });
    }

    return null;
  }

  protected async beforeRecheckAfterFix(
    ctx: StageContext,
    checkName: string,
    fixTaskId: string,
  ): Promise<void> {
    if (checkName !== 'review-passed' || (fixTaskId !== 'repair-review-findings' && fixTaskId !== 'fix-review-findings')) {
      log.info('Legacy check repair completed without artifact invalidation', {
        issueNumber: ctx.issue.number,
        checkName,
        fixTaskId,
      });
      return;
    }
    this.invalidateReviewArtifactForRereview(ctx);
  }

  private invalidateReviewArtifactForRereview(ctx: StageContext): void {
    const changeDir = ctx.artifactManager.getChangeDir(ctx.issue.number);
    if (!changeDir) return;
    const reviewPath = `${changeDir}/review.md`;
    try {
      if (fs.existsSync(reviewPath)) {
        const staleReviewPath = `${changeDir}/review.stale-${Date.now()}.md`;
        fs.renameSync(reviewPath, staleReviewPath);
        log.info('Renamed stale review.md before re-review', { issueNumber: ctx.issue.number, staleReviewPath });
      }
      ctx.checkpointManager?.deleteStep?.(ctx.issue.number, 'check', 'ai-review');
      log.info('Invalidated ai-review checkpoint for re-review', { issueNumber: ctx.issue.number });
    } catch (err) {
      log.warn('Failed to invalidate review artifact before re-review', {
        issueNumber: ctx.issue.number,
        error: err instanceof Error ? err.message : String(err),
      });
    }
  }

  protected isApprovalCheck(checkName: string): boolean {
    return checkName === 'user-approval';
  }

  protected async executeTasks(ctx: StageContext): Promise<unknown> {
    if (!this.usesDefaultChecks) {
      return { done: true };
    }

    const changeDir = ctx.artifactManager.getChangeDir(ctx.issue.number);
    if (!changeDir) {
      throw new Error(`Change directory not found for issue #${ctx.issue.number}`);
    }

    const reviewOutputPath = 'review.md';
    const requestedTask = ctx.workflowRun
      ?.stageRuns.find(stageRun => stageRun.stage === Stage.Check)
      ?.tasks.find(candidate => candidate.taskId === 'ai-review');
    if (ctx.requestedWork?.kind === 'task' && ctx.requestedWork.taskId === 'ai-review' && requestedTask?.status === 'pending') {
      this.invalidateReviewArtifactForRereview(ctx);
    }

    const task = {
      type: 'ai-review',
      label: 'ai-review',
      outputPath: changeDir + '/' + reviewOutputPath,
      verifyArtifact: () => validateReviewArtifact(changeDir, reviewOutputPath).valid,
      buildPrompt: (issue: import('../types').Issue, dir: string) => buildReviewerPrompt(issue, dir),
    };

    const resumeSteps = ctx.checkpointManager
      ? ctx.checkpointManager.getResumeSteps(ctx.issue.number, 'check')
      : [];
    const completedSteps = [...resumeSteps];

    const roundState = { type: '', index: 0 };

    const checkBridgeObserver = {
      onRawNotification(_ctx: import('../agent-runtime/session-observer').SessionContext, notification: import('@agentclientprotocol/sdk').SessionNotification) {
        ctx.emit('plan_session_update', {
          issueId: String(ctx.acpOptions.issueNumber ?? ctx.acpOptions.issueId ?? ''),
          projectId: ctx.issue.projectId,
          roundType: roundState.type,
          roundIndex: roundState.index,
          sessionUpdate: notification.update.sessionUpdate,
          data: notification.update as unknown,
        });
      },
    };

    const wfObservers = createWorkflowSessionObservers({
      eventBus: ctx.eventBus,
      workflowLogRepo: ctx.workflowLogRepo,
      sessionStreamLogRepo: ctx.sessionStreamLogRepo,
      coderSessionRepo: ctx.coderSessionRepo,
      stage: 'check',
      title: 'Check stage',
    }, [checkBridgeObserver]);

    const connectionOptions: AgentSessionOptions = {
      ...ctx.acpOptions,
      issueId: ctx.issue.id,
      issueNumber: ctx.issue.number,
      projectId: ctx.issue.projectId,
      executionId: `check-${ctx.issue.number}`,
      stage: 'check',
      title: 'Check stage',
      observers: wfObservers,
    };

    let session: AgentSession | undefined;

    try {
      session = await AgentSession.create(connectionOptions);

      roundState.type = task.type;
      roundState.index = 0;

      if (completedSteps.includes(task.type)) {
        if (task.verifyArtifact()) {
          log.info('Review task restored from checkpoint', {
            artifact: task.type,
            issueNumber: ctx.issue.number,
          });
          this.appendTaskResult(ctx, {
            taskId: task.type,
            title: task.label,
            status: 'completed',
            artifacts: [],
            attempts: 0,
            duration: 0,
          });
        } else {
          log.info('Review task in checkpoint but artifact missing, re-running', {
            artifact: task.type,
            issueNumber: ctx.issue.number,
          });
          const idx = completedSteps.indexOf(task.type);
          completedSteps.splice(idx);
        }
      } else if (task.verifyArtifact()) {
        log.info('Review artifact exists but not in checkpoint, marking complete', {
          artifact: task.type,
          issueNumber: ctx.issue.number,
        });
        completedSteps.push(task.type);
        ctx.checkpointManager?.markStepComplete(
          ctx.issue.number,
          'check',
          task.type,
          null,
        );
        this.appendTaskResult(ctx, {
          taskId: task.type,
          title: task.label,
          status: 'completed',
          artifacts: [task.label],
          attempts: 0,
          duration: 0,
        });
      } else {
        log.info('Review task', { artifact: task.type, issueNumber: ctx.issue.number });

        emitReviewRoundStart(ctx, task.type, 0, ctx.acpOptions, ctx.issue.projectId ?? '');
        emitStageTaskUpdate(ctx.eventBus, ctx.issue.id, ctx.issue.projectId ?? '', 'check', task.type, task.label, 'started', 1, []);

        const taskStartTime = Date.now();
        let attempts = 1;

        const prompt = task.buildPrompt(ctx.issue, changeDir);
        const result = await session.execute(prompt, { kind: 'task', title: task.label });

        if (!result.success) {
          log.error('Review task failed', { artifact: task.type, error: result.error });
          emitStageTaskUpdate(ctx.eventBus, ctx.issue.id, ctx.issue.projectId ?? '', 'check', task.type, task.label, 'failed', attempts, []);
          this.appendTaskResult(ctx, {
            taskId: task.type,
            title: task.label,
            status: 'failed',
            artifacts: [],
            attempts,
            duration: Date.now() - taskStartTime,
          });
          await session.close();
          throw new Error(`Task "${task.label}" failed: ${result.error ?? 'unknown error'}`);
        }

        if (!task.verifyArtifact()) {
          log.warn('Review artifact not found after task, sending retry', {
            artifact: task.label,
            taskIndex: 0,
          });

          const retryPrompt = [
            `The artifact file ${task.outputPath} was not found. You MUST create it now.`,
            '',
            `Use the write_file tool to write the ${task.type} artifact to:`,
            task.outputPath,
            '',
            'This is a retry. The pipeline cannot continue without this file.',
          ].join('\n');

          log.info('Review retry prompt sent', { artifact: task.type, taskIndex: 0 });
          attempts++;
          emitStageTaskUpdate(ctx.eventBus, ctx.issue.id, ctx.issue.projectId ?? '', 'check', task.type, task.label, 'retrying', attempts, []);

          const retryResult = await session.execute(retryPrompt, { kind: 'retry', title: task.label });

          if (!retryResult.success) {
            log.error('Review retry prompt failed', { artifact: task.type, error: retryResult.error });
            emitStageTaskUpdate(ctx.eventBus, ctx.issue.id, ctx.issue.projectId ?? '', 'check', task.type, task.label, 'failed', attempts, []);
            this.appendTaskResult(ctx, {
              taskId: task.type,
              title: task.label,
              status: 'failed',
              artifacts: [],
              attempts,
              duration: Date.now() - taskStartTime,
            });
            await session.close();
            throw new Error(`Task "${task.label}" retry failed: ${retryResult.error ?? 'unknown error'}`);
          }

          if (!task.verifyArtifact()) {
            log.error('Review artifact still missing after retry', { artifact: task.label });
            emitStageTaskUpdate(ctx.eventBus, ctx.issue.id, ctx.issue.projectId ?? '', 'check', task.type, task.label, 'failed', attempts, []);
            this.appendTaskResult(ctx, {
              taskId: task.type,
              title: task.label,
              status: 'failed',
              artifacts: [],
              attempts,
              duration: Date.now() - taskStartTime,
            });
            await session.close();
            throw new Error(`Artifact "${task.label}" not found after retry`);
          }

          log.info('Review retry succeeded', { artifact: task.label });
        }

        const artifactValidation = validateReviewArtifact(changeDir, reviewOutputPath);
        if (!artifactValidation.valid) {
          log.error('Review artifact invalid after task', {
            artifact: task.label,
            error: artifactValidation.error,
          });
          emitStageTaskUpdate(ctx.eventBus, ctx.issue.id, ctx.issue.projectId ?? '', 'check', task.type, task.label, 'failed', attempts, []);
          this.appendTaskResult(ctx, {
            taskId: task.type,
            title: task.label,
            status: 'failed',
            artifacts: [],
            attempts,
            duration: Date.now() - taskStartTime,
          });
          await session.close();
          throw new Error(`Artifact "${task.label}" is ${artifactValidation.error ?? 'invalid'}`);
        }

        const taskDuration = Date.now() - taskStartTime;
        const taskArtifacts = task.verifyArtifact() ? [task.label] : [];
        emitStageTaskUpdate(ctx.eventBus, ctx.issue.id, ctx.issue.projectId ?? '', 'check', task.type, task.label, 'completed', attempts, taskArtifacts);
        this.appendTaskResult(ctx, {
          taskId: task.type,
          title: task.label,
          status: 'completed',
          artifacts: taskArtifacts,
          attempts,
          duration: taskDuration,
        });

        completedSteps.push(task.type);
        ctx.checkpointManager?.markStepComplete(
          ctx.issue.number,
          'check',
          task.type,
          null,
        );
      }

      await session.close();
    } catch (err) {
      if (session) {
        try {
          await session.close();
        } catch {
          // ignore cleanup errors
        }
      }
      throw err;
    }

    return { success: true };
  }

  protected async executeReportedTask(
    ctx: StageContext,
    taskId: string,
    failedCheck: import('./stage-context').CheckResult | undefined,
    attempt: number,
  ): Promise<StageTaskResult | null> {
    if (taskId === 'ai-review') {
      const output = await this.executeTasks(ctx);
      return {
        taskId: 'ai-review',
        title: 'ai-review',
        status: 'completed',
        artifacts: ['ai-review'],
        attempts: 1,
        duration: 0,
        output,
        alreadyReported: Boolean(ctx.workflowApplicationService),
      };
    }

    if (taskId === 'check:converge-review-snapshot') {
      return this.runConvergeReviewSnapshotTask(ctx);
    }

    if (taskId === 'rebase-branch') {
      return executeRebaseBranchTask(ctx, attempt);
    }

    if (taskId === 'fix-review-findings' || taskId.startsWith('fix-review-findings:') || taskId === 'repair-review-findings') {
      const adapter = createRepairFixAdapter();
      const project = ctx.projectRepo?.findById(ctx.issue.projectId);
      const worktreePath = project
        ? ctx.worktreeManager.getPath(project.name, ctx.issue.number)
        : null;
      if (!worktreePath) {
        log.warn('executeReportedTask: no worktree path found', { issueNumber: ctx.issue.number });
        return null;
      }
      const result = await adapter.dispatch('fix-review-findings', ctx, {
        worktreePath,
        failedCheck: failedCheck ?? { name: 'review-passed', status: 'fail' as const, output: { verdict: 'FAIL' } },
        attempt,
      });
      const instanceResult = { ...result, taskId };
      if (instanceResult.status === 'completed') this.invalidateReviewArtifactForRereview(ctx);
      return instanceResult;
    }

    if (taskId === 'fix-check-health' || taskId.startsWith('fix-check-health:')) {
      const adapter = createRepairFixAdapter();
      const project = ctx.projectRepo?.findById(ctx.issue.projectId);
      const worktreePath = project
        ? ctx.worktreeManager.getPath(project.name, ctx.issue.number)
        : null;
      if (!worktreePath) {
        log.warn('executeReportedTask: no worktree path found', { issueNumber: ctx.issue.number });
        return null;
      }
      const result = await adapter.dispatch('fix-check-health', ctx, {
        worktreePath,
        failedCheck: failedCheck ?? { name: 'health:check', status: 'fail' as const },
        attempt,
      });
      return { ...result, taskId };
    }

    if (taskId === 'fix-merge-readiness' || taskId.startsWith('fix-merge-readiness:') || taskId === 'repair-merge' || taskId.startsWith('repair-merge:')) {
      const adapter = createRepairFixAdapter();
      const project = ctx.projectRepo?.findById(ctx.issue.projectId);
      const worktreePath = project
        ? ctx.worktreeManager.getPath(project.name, ctx.issue.number)
        : null;
      if (!worktreePath) {
        log.warn('executeReportedTask: no worktree path found', { issueNumber: ctx.issue.number });
        return null;
      }
      const result = await adapter.dispatch('repair-merge', ctx, {
        worktreePath,
        failedCheck: failedCheck ?? { name: 'merge-ready', status: 'fail' as const },
        attempt,
      });
      return { ...result, taskId };
    }

    if (failedCheck) return this.runFixTask(ctx, taskId, failedCheck, attempt);
    return null;
  }

  private async runConvergeReviewSnapshotTask(ctx: StageContext): Promise<StageTaskResult> {
    const startedAt = Date.now();
    const taskId = 'check:converge-review-snapshot';
    const title = 'Converge review snapshot';
    try {
      const project = ctx.projectRepo?.findById(ctx.issue.projectId);
      const worktreePath = project ? ctx.worktreeManager.getPath(project.name, ctx.issue.number) : null;
      if (!worktreePath) throw new Error('Worktree not found');

      const convergence = await ctx.worktreeManager.createCheckConvergenceCommit(worktreePath, ctx.issue.number);
      if (!convergence.success) throw new Error(convergence.error ?? 'Convergence commit failed');

      const output = {
        converged: true,
        snapshotSha: convergence.headSha,
      };
      const latestReview = this.latestReviewPassedFromAggregate(ctx);
      if (latestReview) {
        const authoritative = buildAuthoritativeAiReviewResult({ ...latestReview, output: { ...((latestReview.output as Record<string, unknown>) ?? {}), snapshotSha: convergence.headSha } });
        if (authoritative && ctx.checkSuiteRepo) {
          const suite = ctx.checkSuiteRepo.findActiveByIssueId(ctx.issue.id);
          if (suite) {
            ctx.checkSuiteRepo.updateChecks(suite.id, 'review-passed', { status: 'passed', output: authoritative, ranAt: authoritative.convergedAt });
            ctx.checkSuiteRepo.updateSnapshotSha(suite.id, convergence.headSha);
          }
        }
      }
      return { taskId, title, status: 'completed', artifacts: [], attempts: 1, duration: Date.now() - startedAt, output };
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      return { taskId, title, status: 'failed', artifacts: [], attempts: 1, duration: Date.now() - startedAt, reason: message, output: { error: message } };
    }
  }

  private latestReviewPassedFromAggregate(ctx: StageContext): import('./stage-context').CheckResult | undefined {
    const checkStage = ctx.workflowRun?.stageRuns.find(stageRun => stageRun.stage === Stage.Check);
    const review = checkStage?.checks.find(check => check.checkName === 'review-passed');
    if (!review?.output) return undefined;
    return { name: 'review-passed', status: review.status === 'passed' ? 'pass' : 'fail', output: review.output };
  }

  protected getChecks(): Check[] {
    return this.postTaskChecks;
  }

  protected getNextStage(): Stage {
    return Stage.Integrate;
  }
}

function emitReviewRoundStart(
  ctx: StageContext,
  roundType: string,
  roundIndex: number,
  acpOptions: AgentSessionOptions,
  projectId: string,
): void {
  ctx.emit('plan_round_start', {
    issueId: String(acpOptions.issueNumber ?? acpOptions.issueId ?? ''),
    projectId,
    roundType,
    roundLabel: roundType,
    roundIndex,
  });
}
