import { Stage } from '../types';
import type { CheckFailurePolicy, StageContext, StageRunResult } from './stage-context';
import { emitStageTaskUpdate } from './stage-context';
import { BaseStageRunner } from './base-stage-runner';
import type { Check } from './checks';
import { ReviewPassedCheck } from './checks/review-passed-check';
import { MergeReadyCheck } from './checks/merge-ready-check';
import { UserApprovalCheck } from './checks/user-approval-check';
import { buildReviewerPrompt } from '../agents/artifact-prompt';
import { AgentSession, type AgentSessionOptions } from '../agent-runtime/agent-session';
import { validateReviewArtifact } from './utils';
import { Log } from '../util/log';
import { createWorkflowSessionObservers } from '../agent-runtime';
import { runReviewFixTask } from './review-fix-task';

const log = Log.create({ service: 'check-stage-runner' });

export interface StageRunner {
  canHandle(stage: Stage): boolean;
  run(ctx: StageContext): Promise<StageRunResult>;
}

export interface CheckStageRunnerOptions {
  worktreePath: string;
  checks?: Check[];
}

export class CheckStageRunner extends BaseStageRunner implements StageRunner {
  private postTaskChecks: Check[];
  private usesDefaultChecks: boolean;

  constructor(options: CheckStageRunnerOptions) {
    super();
    this.usesDefaultChecks = !options.checks;
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
    return [];
  }

  protected getCheckFailurePolicies(): CheckFailurePolicy[] {
    return [
      {
        checkName: 'review-passed',
        fixTaskId: 'repair-review-findings',
        maxAttempts: 3,
      },
      {
        checkName: 'merge-ready',
        fixTaskId: 'repair-merge',
        maxAttempts: 2,
      },
    ];
  }

  protected async runFixTask(
    ctx: StageContext,
    _taskId: string,
    failedCheck: import('./stage-context').CheckResult,
    attempt: number,
  ): Promise<import('./stage-context').StageTaskResult | null> {
    if (failedCheck.name === 'review-passed' && (failedCheck.output as { verdict?: string })?.verdict === 'FAIL') {
      const project = ctx.projectRepo?.findById(ctx.issue.projectId);
      const worktreePath = project
        ? ctx.worktreeManager.getPath(project.name, ctx.issue.number)
        : null;
      if (!worktreePath) {
        log.warn('runFixTask: no worktree path found', { issueNumber: ctx.issue.number });
        return null;
      }
      return runReviewFixTask(ctx, { worktreePath, failedCheck, attempt });
    }

    if (failedCheck.name === 'merge-ready') {
      return this.runMergeRepairTask(ctx, failedCheck, attempt);
    }

    return null;
  }

  private async runMergeRepairTask(
    ctx: StageContext,
    failedCheck: import('./stage-context').CheckResult,
    attempt: number,
  ): Promise<import('./stage-context').StageTaskResult> {
    const startedAt = Date.now();
    const taskId = 'repair-merge';
    const title = 'Repair merge readiness';
    const stage = 'check';

    const project = ctx.projectRepo?.findById(ctx.issue.projectId);
    if (!project) {
      return {
        taskId,
        title,
        status: 'failed',
        artifacts: [],
        attempts: attempt,
        duration: Date.now() - startedAt,
        output: { kind: 'merge-repair', success: false, error: 'Project not found' },
      };
    }

    const worktreePath = ctx.worktreeManager.getPath(project.name, ctx.issue.number);
    if (!worktreePath) {
      return {
        taskId,
        title,
        status: 'failed',
        artifacts: [],
        attempts: attempt,
        duration: Date.now() - startedAt,
        output: { kind: 'merge-repair', success: false, error: 'Worktree not found' },
      };
    }

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
      const headBefore = await ctx.worktreeManager.getHeadSha(worktreePath);

      const output = failedCheck.output as { targetBranch?: string; conflictFiles?: string[] };
      const targetBranch = output?.targetBranch ?? project.baseBranch;
      const conflictFiles = output?.conflictFiles ?? [];

      log.info('Running merge repair', {
        issueNumber: ctx.issue.number,
        worktreePath,
        targetBranch,
        conflictFiles,
        attempt,
      });

      const result = await ctx.worktreeManager.rebaseOntoMaster(
        project.path,
        project.name,
        ctx.issue.number,
        targetBranch,
        { abortOnConflict: false },
      );

      const headAfter = await ctx.worktreeManager.getHeadSha(worktreePath);
      const headChanged = headBefore !== headAfter;

      log.info('Merge repair completed', {
        issueNumber: ctx.issue.number,
        success: result.success,
        conflicts: result.conflicts,
        headChanged,
        headBefore,
        headAfter,
      });

      emitStageTaskUpdate(
        ctx.eventBus,
        ctx.issue.id,
        ctx.issue.projectId,
        stage,
        taskId,
        title,
        result.success ? 'completed' : 'failed',
        attempt,
        [],
      );

      return {
        taskId,
        title,
        status: result.success ? 'completed' : 'failed',
        artifacts: [],
        attempts: attempt,
        duration: Date.now() - startedAt,
        output: {
          kind: 'merge-repair',
          targetBranch,
          attempt,
          success: result.success,
          conflicts: result.conflicts,
          headChanged,
          headBefore,
          headAfter,
        },
      };
    } catch (err) {
      const error = err instanceof Error ? err.message : String(err);
      log.warn('Merge repair failed', { issueNumber: ctx.issue.number, taskId, error });

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
        duration: Date.now() - startedAt,
        output: { kind: 'merge-repair', success: false, error },
      };
    }
  }

  protected async beforeRecheckAfterFix(
    ctx: StageContext,
    checkName: string,
    _fixTaskId: string,
  ): Promise<void> {
    const changeDir = ctx.artifactManager.getChangeDir(ctx.issue.number);

    if (checkName === 'review-passed') {
      if (changeDir) {
        const reviewArtifactPath = changeDir + '/review.md';
        try {
          if (require('fs').existsSync(reviewArtifactPath)) {
            require('fs').unlinkSync(reviewArtifactPath);
            log.info('Invalidated stale review.md', { changeDir });
          }
        } catch {
          log.warn('Failed to delete stale review.md', { changeDir });
        }
      }
      ctx.checkpointManager?.deleteStep(ctx.issue.number, 'check', 'ai-review');
      log.info('Invalidated ai-review checkpoint for re-review', {
        issueNumber: ctx.issue.number,
      });
    }

    if (checkName === 'merge-ready') {
      if (changeDir) {
        const reviewArtifactPath = changeDir + '/review.md';
        try {
          if (require('fs').existsSync(reviewArtifactPath)) {
            require('fs').unlinkSync(reviewArtifactPath);
            log.info('Invalidated stale review.md after merge repair', { changeDir });
          }
        } catch {
          log.warn('Failed to delete stale review.md after merge repair', { changeDir });
        }
      }
      ctx.checkpointManager?.deleteStep(ctx.issue.number, 'check', 'ai-review');
      log.info('Invalidated ai-review checkpoint after merge repair, HEAD changed', {
        issueNumber: ctx.issue.number,
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

    const task = {
      type: 'ai-review',
      label: 'ai-review',
      outputPath: changeDir + '/' + reviewOutputPath,
      verifyArtifact: () => validateReviewArtifact(changeDir, reviewOutputPath),
      buildPrompt: (issue: import('../types').Issue, dir: string) => buildReviewerPrompt(issue, dir),
    };

    const resumeSteps = ctx.checkpointManager
      ? ctx.checkpointManager.getResumeSteps(ctx.issue.number, 'check')
      : [];
    const completedSteps = [...resumeSteps];

    const roundState = { type: '', index: 0 };

    const checkBridgeObserver = {
      onRawNotification(_ctx: import('../agent-runtime/session-observer').SessionContext, notification: import('@agentclientprotocol/sdk').SessionNotification) {
        if (!ctx.eventBus) return;
        try {
          ctx.eventBus.emit('plan_session_update', {
            issueId: String(ctx.acpOptions.issueNumber ?? ctx.acpOptions.issueId ?? ''),
            projectId: ctx.issue.projectId,
            roundType: roundState.type,
            roundIndex: roundState.index,
            sessionUpdate: notification.update.sessionUpdate,
            data: notification.update as unknown,
          });
        } catch (e) {
          log.warn('eventBus.emit failed for plan_session_update', {
            error: e instanceof Error ? e.message : String(e),
          });
        }
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
          log.info('Review task skipped (checkpoint + artifact exists)', {
            artifact: task.type,
            issueNumber: ctx.issue.number,
          });
          this.appendTaskResult(ctx, {
            taskId: task.type,
            title: task.label,
            status: 'skipped',
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
          status: 'skipped',
          artifacts: [task.label],
          attempts: 0,
          duration: 0,
        });
      } else {
        log.info('Review task', { artifact: task.type, issueNumber: ctx.issue.number });

        emitReviewRoundStart(ctx.eventBus, task.type, 0, ctx.acpOptions, ctx.issue.projectId ?? '');
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

  protected getChecks(): Check[] {
    return this.postTaskChecks;
  }

  protected getNextStage(): Stage {
    return Stage.Integrate;
  }
}

function emitReviewRoundStart(
  eventBus: import('../services/event-bus').EventBus | undefined,
  roundType: string,
  roundIndex: number,
  acpOptions: AgentSessionOptions,
  projectId: string,
): void {
  if (!eventBus) return;
  try {
    eventBus.emit('plan_round_start', {
      issueId: String(acpOptions.issueNumber ?? acpOptions.issueId ?? ''),
      projectId,
      roundType,
      roundLabel: roundType,
      roundIndex,
    });
  } catch (e) {
    log.warn('eventBus.emit failed for plan_round_start', {
      roundType,
      error: e instanceof Error ? e.message : String(e),
    });
  }
}
