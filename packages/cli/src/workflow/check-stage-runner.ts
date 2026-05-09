import { Stage } from '../types';
import type { CheckFailurePolicy, CheckResult, StageContext, StageRunResult, StageTaskResult } from './stage-context';
import { emitStageTaskUpdate } from './stage-context';
import { BaseStageRunner } from './base-stage-runner';
import type { Check } from './checks';
import { AiReviewCheck } from './checks/ai-review-check';
import { UserApprovalCheck } from './checks/user-approval-check';
import { buildReviewerPrompt, buildReviewSelfCheckPrompt } from '../agents/artifact-prompt';
import { AgentSession, type AgentSessionOptions } from '../agent-runtime/agent-session';
import { readReportFile } from './utils';
import { Log } from '../util/log';
import { createWorkflowSessionObservers } from '../agent-runtime';
import { loadHealthGatePolicies, loadWorkflow } from './workflow-loader';
import { HealthGateCheck } from './checks/health-gate-check';
import { MergeReadinessCheck } from './checks/merge-readiness-check';
import { IntegrationHealthGatePreviewCheck } from './checks/integration-health-gate-preview-check';
import { runHealthFixTask } from './health-fix-task';
import { runReviewFixTask } from './review-fix-task';

const log = Log.create({ service: 'check-stage-runner' });

interface TaskConfig {
  type: string;
  label: string;
  outputPath: string;
  verifyArtifact: () => boolean;
  buildPrompt: (issue: import('../types').Issue, changeDir: string) => string;
}

export interface StageRunner {
  canHandle(stage: Stage): boolean;
  run(ctx: StageContext): Promise<StageRunResult>;
}

export interface CheckStageRunnerOptions {
  worktreePath: string;
  checks?: Check[];
}

export class CheckStageRunner extends BaseStageRunner implements StageRunner {
  private worktreePath: string;
  private preTaskChecks: Check[];
  private postTaskChecks: Check[];
  private usesDefaultChecks: boolean;
  private checkHealthGatePolicy: import('./workflow-loader').HealthGatePolicy;

  constructor(options: CheckStageRunnerOptions) {
    super();
    this.worktreePath = options.worktreePath;
    this.usesDefaultChecks = !options.checks;
    const wf = loadWorkflow(this.worktreePath);
    this.checkHealthGatePolicy = typeof wf === 'string'
      ? { enabled: true, command: 'npm run build && npm test', timeout: 300000, autoFix: true, maxFixAttempts: 2, fallbackReaction: { type: 'escalate', escalateTarget: Stage.Check } }
      : loadHealthGatePolicies(wf).check;
    this.preTaskChecks = options.checks ? [] : [
      new HealthGateCheck({ worktreePath: this.worktreePath, policy: this.checkHealthGatePolicy, stage: 'check' }),
      new MergeReadinessCheck(),
      new IntegrationHealthGatePreviewCheck(),
    ];
    this.postTaskChecks = options.checks ?? [
      new AiReviewCheck(),
      new UserApprovalCheck(Stage.Check),
    ];
  }

  canHandle(stage: Stage): boolean {
    return stage === Stage.Check;
  }

  protected getPreTaskChecks(): Check[] {
    return this.preTaskChecks;
  }

  protected getCheckFailurePolicies(): CheckFailurePolicy[] {
    const policies: CheckFailurePolicy[] = [];

    if (this.checkHealthGatePolicy.enabled && this.checkHealthGatePolicy.autoFix) {
      policies.push({
        checkName: 'health:check',
        fixTaskId: 'fix-check-health',
        maxAttempts: this.checkHealthGatePolicy.maxFixAttempts,
      });
    }

    policies.push({
      checkName: 'ai-review',
      fixTaskId: 'fix-review-findings',
      maxAttempts: 1,
    });

    return policies;
  }

  protected async runFixTask(
    ctx: StageContext,
    taskId: string,
    failedCheck: CheckResult,
    attempt: number,
  ): Promise<StageTaskResult | null> {
    if (taskId === 'fix-check-health') {
      return runHealthFixTask(ctx, {
        taskId: 'fix-check-health',
        title: 'Fix check health',
        stage: 'check',
        worktreePath: this.worktreePath,
        healthCommand: this.checkHealthGatePolicy.command,
        failedCheck,
        attempt,
      });
    }

    if (taskId === 'fix-review-findings') {
      return runReviewFixTask(ctx, {
        worktreePath: this.worktreePath,
        failedCheck,
        attempt,
      });
    }

    return null;
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
    const selfCheckOutputPath = 'review-self-check.md';

    const tasks: TaskConfig[] = [
      {
        type: 'review',
        label: 'review.md',
        outputPath: changeDir + '/' + reviewOutputPath,
        verifyArtifact: () => readReportFile(changeDir, reviewOutputPath) !== null,
        buildPrompt: (issue, dir) => buildReviewerPrompt(issue, dir),
      },
      {
        type: 'review-self-check',
        label: 'review-self-check.md',
        outputPath: changeDir + '/' + selfCheckOutputPath,
        verifyArtifact: () => readReportFile(changeDir, selfCheckOutputPath) !== null,
        buildPrompt: (issue, dir) => buildReviewSelfCheckPrompt(issue, dir),
      },
    ];

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

      for (const [index, task] of tasks.entries()) {
        roundState.type = task.type;
        roundState.index = index;

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
            continue;
          }
          log.info('Review task in checkpoint but artifact missing, re-running', {
            artifact: task.type,
            issueNumber: ctx.issue.number,
          });
          const idx = completedSteps.indexOf(task.type);
          completedSteps.splice(idx);
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
            tasks[index + 1]?.type ?? null,
          );
          this.appendTaskResult(ctx, {
            taskId: task.type,
            title: task.label,
            status: 'skipped',
            artifacts: [task.label],
            attempts: 0,
            duration: 0,
          });
          continue;
        }

        log.info('Review task', { artifact: task.type, issueNumber: ctx.issue.number });

        emitReviewRoundStart(ctx.eventBus, task.type, index, ctx.acpOptions, ctx.issue.projectId ?? '');
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
            taskIndex: index,
          });

          const retryPrompt = [
            `The artifact file ${task.outputPath} was not found. You MUST create it now.`,
            '',
            `Use the write_file tool to write the ${task.type} artifact to:`,
            task.outputPath,
            '',
            'This is a retry. The pipeline cannot continue without this file.',
          ].join('\n');

          log.info('Review retry prompt sent', { artifact: task.type, taskIndex: index });
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
          tasks[index + 1]?.type ?? null,
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
