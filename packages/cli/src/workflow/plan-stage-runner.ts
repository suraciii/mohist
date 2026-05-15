import * as path from 'path';
import * as fs from 'fs';
import { execFile } from 'child_process';
import { promisify } from 'util';
import { Stage } from '../types';
import { buildArtifactPrompt, buildSelfReviewPrompt } from '../agents/artifact-prompt';
import { AgentSession, type AgentSessionOptions } from '../agent-runtime/agent-session';
import { readReportFile } from './utils';
import { Log } from '../util/log';
import { BaseStageRunner } from './base-stage-runner';
import type { CheckResult, StageContext, StageRunResult, StageTaskResult } from './stage-context';
import { emitStageTaskUpdate } from './stage-context';
import type { Check } from './checks';
import { ProposalCompleteCheck } from './checks/proposal-complete-check';
import { SpecsCompleteCheck } from './checks/specs-complete-check';
import { DesignCompleteCheck } from './checks/design-complete-check';
import { TasksValidCheck } from './checks/tasks-valid-check';
import { SelfReviewPassedCheck } from './checks/self-review-passed-check';
import { UserApprovalCheck } from './checks/user-approval-check';
import { isCurrentStageApproval } from './issue-lifecycle';
import { createWorkflowSessionObservers } from '../agent-runtime';
import { HealthGateCheck } from './checks/health-gate-check';
import { loadHealthGatePolicies, loadWorkflow } from './workflow-loader';
import { createRepairFixAdapter } from './task-runtime/repair-fix-adapter';
import { executeRebaseBranchTask } from './task-runtime/rebase-task-handler';

const execFileAsync = promisify(execFile);
const log = Log.create({ service: 'plan-stage' });

function normalizeRejectionFeedback(output: unknown): string | null {
  if (typeof output === 'string' && output.trim()) return output;
  if (output && typeof output === 'object' && 'feedback' in output) {
    const feedback = (output as { feedback: unknown }).feedback;
    if (typeof feedback === 'string' && feedback.trim()) return feedback;
  }
  return null;
}

function extractRejectionFeedback(workflowRun: StageContext['workflowRun'], stage: string, retryFeedback?: unknown): string | null {
  const preservedFeedback = normalizeRejectionFeedback(retryFeedback);
  if (preservedFeedback) return preservedFeedback;
  if (!workflowRun) return null;
  const stageRun = workflowRun.stageRuns?.find(sr => sr.stage === stage);
  if (!stageRun) return null;
  if (stageRun.approvalStatus !== 'rejected') return null;
  return normalizeRejectionFeedback(stageRun.approvalOutput);
}

function isRejectedPlanRetry(workflowRun: StageContext['workflowRun'], retryFeedback?: unknown): boolean {
  if (normalizeRejectionFeedback(retryFeedback)) return true;
  if (!workflowRun) return false;
  const stageRun = workflowRun.stageRuns?.find(sr => sr.stage === 'plan');
  return stageRun?.approvalStatus === 'rejected';
}

interface TaskConfig {
  type: string;
  label: string;
  outputPath: string;
  verifyArtifact: () => boolean;
  buildPrompt: (issue: import('../types').Issue, changeDir: string, feedback?: string) => string;
}

type PlanRejectionFeedback = {
  shouldReplan: boolean;
  message?: string;
};

type WorkflowRunRejectionEvent = {
  type?: string;
  reason?: {
    reason?: string;
    stage?: string;
    message?: string;
  };
};

export class PlanStageRunner extends BaseStageRunner {
  private worktreePath: string;
  private planHealthGatePolicy: import('./workflow-loader').HealthGatePolicy;
  private readonly aggregateTaskSessions = new Map<string, AgentSession>();
  private readonly aggregateTaskAbortListeners = new Map<string, () => void>();

  constructor(worktreePath: string = '') {
    super();
    this.worktreePath = worktreePath;
    const wf = loadWorkflow(worktreePath);
    this.planHealthGatePolicy = typeof wf === 'string'
      ? { enabled: true, command: 'npm run typecheck', timeout: 300000, autoFix: false, maxFixAttempts: 0, fallbackReaction: { type: 'ask-user' } }
      : loadHealthGatePolicies(wf).plan;
  }

  canHandle(stage: Stage): boolean {
    return stage === Stage.Plan || stage === Stage.Backlog;
  }

  protected isApprovalCheck(checkName: string): boolean {
    return checkName === 'user-approval';
  }

  protected async executeReportedTask(
    ctx: StageContext,
    taskId: string,
    failedCheck: CheckResult | undefined,
    attempt: number,
  ): Promise<StageTaskResult | null> {
    if (taskId === 'rebase-branch') {
      return executeRebaseBranchTask(ctx, attempt);
    }

    if (taskId === 'repair-plan-artifacts' || taskId === 'fix-plan-health') {
      const adapter = createRepairFixAdapter();
      const worktreePath = this.worktreePath || ctx.worktreeManager.getPath(
        ctx.projectRepo?.findById(ctx.issue.projectId)?.name ?? '',
        ctx.issue.number,
      ) || process.cwd();
      return adapter.dispatch(taskId as any, ctx, {
        worktreePath,
        failedCheck: failedCheck ?? { name: taskId, status: 'fail' as const },
        attempt,
      });
    }
    const changeDir = ctx.artifactManager.getChangeDir(ctx.issue.number)
      || ctx.artifactManager.createChangeDir(ctx.issue.number, ctx.issue.title);
    if (!changeDir) {
      throw new Error(`Failed to get or create change directory for issue #${ctx.issue.number}`);
    }

    const tasks = this.createTaskConfigs(changeDir);
    const taskIndex = tasks.findIndex(task => task.type === taskId);
    if (taskIndex === -1) return null;

    return this.executeSingleArtifactTask(ctx, tasks, taskIndex, changeDir);
  }

  private createTaskConfigs(changeDir: string): TaskConfig[] {
    return [
      {
        type: 'proposal',
        label: 'proposal.md',
        outputPath: path.join(changeDir, 'proposal.md'),
        verifyArtifact: () => fs.existsSync(path.join(changeDir, 'proposal.md')),
        buildPrompt: (iss, dir, feedback) => buildArtifactPrompt('proposal', iss, dir, undefined, { feedback }),
      },
      {
        type: 'specs',
        label: 'specs/',
        outputPath: path.join(changeDir, 'specs'),
        verifyArtifact: () => fs.existsSync(path.join(changeDir, 'specs')),
        buildPrompt: (iss, dir, feedback) => buildArtifactPrompt('specs', iss, dir, undefined, { feedback }),
      },
      {
        type: 'design',
        label: 'design.md',
        outputPath: path.join(changeDir, 'design.md'),
        verifyArtifact: () => fs.existsSync(path.join(changeDir, 'design.md')),
        buildPrompt: (iss, dir, feedback) => buildArtifactPrompt('design', iss, dir, undefined, { feedback }),
      },
      {
        type: 'tasks',
        label: 'tasks.json',
        outputPath: path.join(changeDir, 'tasks.json'),
        verifyArtifact: () => fs.existsSync(path.join(changeDir, 'tasks.json')),
        buildPrompt: (iss, dir, feedback) => buildArtifactPrompt('tasks', iss, dir, undefined, { feedback }),
      },
      {
        type: 'self-review',
        label: 'self-review.md',
        outputPath: path.join(changeDir, 'self-review.md'),
        verifyArtifact: () => fs.existsSync(path.join(changeDir, 'self-review.md')),
        buildPrompt: (iss, dir, feedback) => buildSelfReviewPrompt(iss, dir, undefined, feedback),
      },
    ];
  }

  private async executeSingleArtifactTask(
    ctx: StageContext,
    tasks: TaskConfig[],
    taskIndex: number,
    changeDir: string,
  ): Promise<StageTaskResult> {
    const { issue, acpOptions, eventBus, checkpointManager } = ctx;
    const task = tasks[taskIndex];
    const completedSteps = checkpointManager.getResumeSteps(issue.number, 'plan');
    const isLastTask = taskIndex === tasks.length - 1;
    const feedback = this.getPlanRejectionFeedback(ctx);
    const forceFreshAttempt = feedback.shouldReplan || isRejectedPlanRetry(ctx.workflowRun, ctx.rejectionFeedback);

    if (!forceFreshAttempt && completedSteps.includes(task.type) && task.verifyArtifact()) {
      if (isLastTask) await this.closeAggregateTaskSession(ctx);
      return { taskId: task.type, title: task.label, status: 'completed', artifacts: [], attempts: 0, duration: 0 };
    }
    const session = await this.getAggregateTaskSession(ctx);

    const startedAt = Date.now();
    let attempts = 1;
    try {
      emitRoundStart(ctx, task.type, taskIndex, acpOptions, issue.projectId ?? '');
      emitStageTaskUpdate(eventBus, issue.id, issue.projectId ?? '', 'plan', task.type, task.label, 'started', attempts, []);
      const result = await session.execute(task.buildPrompt(issue, changeDir, feedback.message), { kind: 'task', title: task.label });
      if (!result.success) {
        emitStageTaskUpdate(eventBus, issue.id, issue.projectId ?? '', 'plan', task.type, task.label, 'failed', attempts, []);
        await this.closeAggregateTaskSession(ctx);
        return { taskId: task.type, title: task.label, status: 'failed', artifacts: [], attempts, duration: Date.now() - startedAt, reason: `Task "${task.label}" failed: ${result.error ?? 'unknown error'}` };
      }
      if (!task.verifyArtifact()) {
        attempts++;
        emitStageTaskUpdate(eventBus, issue.id, issue.projectId ?? '', 'plan', task.type, task.label, 'retrying', attempts, []);
        const retryPrompt = [
          `The artifact file ${task.outputPath} was not found. You MUST create it now.`,
          '',
          `Use the write_file tool to write the ${task.type} artifact to:`,
          task.outputPath,
          '',
          'This is a retry. The pipeline cannot continue without this file.',
        ].join('\n');
        const retryResult = await session.execute(retryPrompt, { kind: 'retry', title: task.label });
        if (!retryResult.success || !task.verifyArtifact()) {
          emitStageTaskUpdate(eventBus, issue.id, issue.projectId ?? '', 'plan', task.type, task.label, 'failed', attempts, []);
          await this.closeAggregateTaskSession(ctx);
          return {
            taskId: task.type,
            title: task.label,
            status: 'failed',
            artifacts: [],
            attempts,
            duration: Date.now() - startedAt,
            reason: retryResult.success ? `Artifact "${task.label}" not found after retry` : `Task "${task.label}" retry failed: ${retryResult.error ?? 'unknown error'}`,
          };
        }
      }
      const artifacts = task.verifyArtifact() ? [task.label] : [];
      checkpointManager.markStepComplete(issue.number, 'plan', task.type, tasks[taskIndex + 1]?.type ?? null);
      emitStageTaskUpdate(eventBus, issue.id, issue.projectId ?? '', 'plan', task.type, task.label, 'completed', attempts, artifacts);
      if (isLastTask) await this.closeAggregateTaskSession(ctx);
      return { taskId: task.type, title: task.label, status: 'completed', artifacts, attempts, duration: Date.now() - startedAt };
    } catch (err) {
      await this.closeAggregateTaskSession(ctx);
      throw err;
    }
  }

  private aggregateTaskSessionKey(ctx: StageContext): string {
    return `${ctx.issue.id}:plan:${ctx.acpOptions.cwd ?? ''}`;
  }

  private async getAggregateTaskSession(ctx: StageContext): Promise<AgentSession> {
    const key = this.aggregateTaskSessionKey(ctx);
    const existing = this.aggregateTaskSessions.get(key);
    if (existing && existing.canClose()) return existing;
    if (existing) await this.closeAggregateTaskSession(ctx);

    const wfObservers = createWorkflowSessionObservers({
      eventBus: ctx.eventBus,
      workflowLogRepo: ctx.workflowLogRepo,
      sessionStreamLogRepo: ctx.sessionStreamLogRepo,
      coderSessionRepo: ctx.coderSessionRepo,
      stage: 'plan',
      title: 'Plan stage',
    }, [this.createPlanBridgeObserver(ctx)]);

    const session = await AgentSession.create({
      ...ctx.acpOptions,
      issueId: ctx.issue.id,
      projectId: ctx.issue.projectId,
      issueNumber: ctx.issue.number,
      executionId: `plan-${ctx.issue.number}`,
      stage: 'plan',
      title: 'Plan stage',
      observers: wfObservers,
    });
    this.aggregateTaskSessions.set(key, session);
    this.registerAggregateTaskAbortListener(ctx, key);
    return session;
  }

  private registerAggregateTaskAbortListener(ctx: StageContext, key: string): void {
    const signal = ctx.acpOptions.signal ?? ctx.signal;
    if (!signal || this.aggregateTaskAbortListeners.has(key)) return;
    const onAbort = () => {
      void this.closeAggregateTaskSession(ctx);
    };
    this.aggregateTaskAbortListeners.set(key, onAbort);
    signal.addEventListener('abort', onAbort, { once: true });
  }

  private async closeAggregateTaskSession(ctx: StageContext): Promise<void> {
    const key = this.aggregateTaskSessionKey(ctx);
    const signal = ctx.acpOptions.signal ?? ctx.signal;
    const abortListener = this.aggregateTaskAbortListeners.get(key);
    if (signal && abortListener) {
      signal.removeEventListener('abort', abortListener);
      this.aggregateTaskAbortListeners.delete(key);
    }
    const session = this.aggregateTaskSessions.get(key);
    if (!session) return;
    this.aggregateTaskSessions.delete(key);
    try {
      await session.close();
    } catch (err) {
      log.warn('Failed to close aggregate plan task session', {
        issueNumber: ctx.issue.number,
        error: err instanceof Error ? err.message : String(err),
      });
    }
  }

  private createPlanBridgeObserver(ctx: StageContext) {
    return {
      onRawNotification(_sessionCtx: import('../agent-runtime/session-observer').SessionContext, notification: import('@agentclientprotocol/sdk').SessionNotification) {
        if (!ctx.eventBus) return;
        try {
          ctx.eventBus.emit('plan_session_update', {
            issueId: String(ctx.acpOptions.issueNumber ?? ctx.acpOptions.issueId ?? ''),
            projectId: ctx.issue.projectId,
            roundType: '',
            roundIndex: 0,
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
  }

  protected async executeTasks(ctx: StageContext): Promise<unknown> {
    const { issue, acpOptions, artifactManager, eventBus, checkpointManager } = ctx;

    const changeDir = artifactManager.getChangeDir(issue.number)
      || artifactManager.createChangeDir(issue.number, issue.title);
    if (!changeDir) {
      throw new Error(`Failed to get or create change directory for issue #${issue.number}`);
    }

    if (isCurrentStageApproval(issue, Stage.Plan, 'approved')) {
      checkpointManager.delete(issue.number, 'plan');
      return { changeDir, skipped: true };
    }

    const resumeSteps = checkpointManager.getResumeSteps(issue.number, 'plan');
    const feedback = this.getPlanRejectionFeedback(ctx);
    if (!feedback.message) {
      const contextFeedback = normalizeRejectionFeedback(ctx.rejectionFeedback);
      if (contextFeedback) {
        feedback.shouldReplan = true;
        feedback.message = contextFeedback;
      }
    }

    const tasks = this.createTaskConfigs(changeDir);
    const forceFreshAttempt = feedback.shouldReplan || isRejectedPlanRetry(ctx.workflowRun, ctx.rejectionFeedback);

    const completedSteps = [...resumeSteps];

const planBridgeObserver = {
      onRawNotification(_ctx: import('../agent-runtime/session-observer').SessionContext, notification: import('@agentclientprotocol/sdk').SessionNotification) {
        ctx.emit('plan_session_update', {
          issueId: String(acpOptions.issueNumber ?? acpOptions.issueId ?? ''),
          projectId: issue.projectId,
          roundType: '',
          roundIndex: 0,
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
      stage: 'plan',
      title: 'Plan stage',
    }, [planBridgeObserver]);

    const connectionOptions: AgentSessionOptions = {
      ...acpOptions,
      issueId: issue.id,
      projectId: issue.projectId,
      issueNumber: issue.number,
      executionId: `plan-${issue.number}`,
      stage: 'plan',
      title: 'Plan stage',
      observers: wfObservers,
    };

    let session: AgentSession | undefined;

    try {
      session = await AgentSession.create(connectionOptions);

      for (const [index, task] of tasks.entries()) {
        if (!forceFreshAttempt && completedSteps.includes(task.type)) {
          if (task.verifyArtifact()) {
            log.info('Plan task restored from checkpoint', {
              artifact: task.type,
              issueNumber: issue.number,
            });
            this.appendTaskResult(ctx, {
              taskId: task.type,
              title: task.label,
              status: 'completed',
              artifacts: [],
              attempts: 0,
              duration: 0,
            });
            continue;
          }
          log.info('Plan task in checkpoint but artifact missing, re-running', {
            artifact: task.type,
            issueNumber: issue.number,
          });
          const idx = completedSteps.indexOf(task.type);
          completedSteps.splice(idx);
        } else if (!forceFreshAttempt && resumeSteps.length > 0 && task.verifyArtifact()) {
          log.info('Plan artifact exists but not in checkpoint, marking complete', {
            artifact: task.type,
            issueNumber: issue.number,
          });
          completedSteps.push(task.type);
          checkpointManager.markStepComplete(
            issue.number,
            'plan',
            task.type,
            tasks[index + 1]?.type ?? null,
          );
          this.appendTaskResult(ctx, {
            taskId: task.type,
            title: task.label,
            status: 'completed',
            artifacts: [task.label],
            attempts: 0,
            duration: 0,
          });
          continue;
        }

        log.info('Plan task', { artifact: task.type, issueNumber: issue.number });

        emitRoundStart(ctx, task.type, index, acpOptions, issue.projectId ?? '');
        emitStageTaskUpdate(eventBus, issue.id, issue.projectId ?? '', 'plan', task.type, task.label, 'started', 1, []);

        const taskStartTime = Date.now();
        let attempts = 1;

        const prompt = task.buildPrompt(issue, changeDir, feedback.message);
        const result = await session.execute(prompt, { kind: 'task', title: task.label });

        if (!result.success) {
          log.error('Plan task failed', { artifact: task.type, error: result.error });
          emitStageTaskUpdate(eventBus, issue.id, issue.projectId ?? '', 'plan', task.type, task.label, 'failed', attempts, []);
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
          log.warn('Plan artifact not found after task, sending retry', {
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

          log.info('Plan retry prompt sent', { artifact: task.type, taskIndex: index });
          attempts++;
          emitStageTaskUpdate(eventBus, issue.id, issue.projectId ?? '', 'plan', task.type, task.label, 'retrying', attempts, []);

          const retryResult = await session.execute(retryPrompt, { kind: 'retry', title: task.label });

          if (!retryResult.success) {
            log.error('Plan retry prompt failed', { artifact: task.type, error: retryResult.error });
            emitStageTaskUpdate(eventBus, issue.id, issue.projectId ?? '', 'plan', task.type, task.label, 'failed', attempts, []);
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
            log.error('Plan artifact still missing after retry', { artifact: task.label });
            emitStageTaskUpdate(eventBus, issue.id, issue.projectId ?? '', 'plan', task.type, task.label, 'failed', attempts, []);
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

          log.info('Plan retry succeeded', { artifact: task.label });
        }

        const taskDuration = Date.now() - taskStartTime;
        const taskArtifacts = task.verifyArtifact() ? [task.label] : [];
        emitStageTaskUpdate(eventBus, issue.id, issue.projectId ?? '', 'plan', task.type, task.label, 'completed', attempts, taskArtifacts);
        this.appendTaskResult(ctx, {
          taskId: task.type,
          title: task.label,
          status: 'completed',
          artifacts: taskArtifacts,
          attempts,
          duration: taskDuration,
        });

        completedSteps.push(task.type);
        checkpointManager.markStepComplete(
          issue.number,
          'plan',
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

    const commitOk = await commitPlanArtifacts(changeDir, issue);
    if (!commitOk) {
      throw new Error(`Failed to commit plan artifacts for issue #${issue.number}`);
    }

    checkpointManager.delete(issue.number, 'plan');

    return { changeDir };
  }

  private getPlanRejectionFeedback(ctx: StageContext): PlanRejectionFeedback {
    if (ctx.issue.stage !== Stage.Plan) return { shouldReplan: false };
    const message = normalizeRejectionFeedback(ctx.rejectionFeedback)
      ?? extractRejectionFeedback(ctx.workflowRun, 'plan')
      ?? this.findLatestPlanRejectionMessage(ctx);
    if (!message) {
      return { shouldReplan: false };
    }
    return { shouldReplan: true, message };
  }

  private findLatestPlanRejectionMessage(ctx: StageContext): string | undefined {
    if (!ctx.workflowLogRepo) return undefined;
    const entries = ctx.workflowLogRepo.findByIssueId(ctx.issue.id, 'workflow_run.approval-rejected');
    for (let i = entries.length - 1; i >= 0; i--) {
      const entry = entries[i];
      try {
        const event = JSON.parse(entry.data) as WorkflowRunRejectionEvent;
        const message = event.reason?.stage === Stage.Plan && event.reason.reason === 'approval-rejected'
          ? event.reason.message?.trim()
          : undefined;
        if (message) return message;
      } catch {
        // Ignore malformed historical log entries.
      }
    }
    return undefined;
  }

  protected getChecks(): Check[] {
    const changeDir = this._getChangeDirForHealthGate();
    return [
      new ProposalCompleteCheck(),
      new SpecsCompleteCheck(),
      new DesignCompleteCheck(),
      new TasksValidCheck(),
      new SelfReviewPassedCheck(),
      new HealthGateCheck({
        worktreePath: changeDir,
        policy: this.planHealthGatePolicy,
        stage: 'plan',
      }),
      new UserApprovalCheck(Stage.Plan),
    ];
  }

  private _getChangeDirForHealthGate(): string {
    try {
      const worktree = this.worktreePath || process.cwd();
      const changeDir = require('../openspec/detector').findChangeDir(worktree, 0);
      return changeDir ? path.dirname(path.dirname(changeDir)) : worktree;
    } catch {
      return this.worktreePath || process.cwd();
    }
  }

  protected getNextStage(): Stage {
    return Stage.Build;
  }

  async run(ctx: StageContext): Promise<StageRunResult> {
    const result = await super.run(ctx);

    if (result.success) {
      const changeDir = ctx.artifactManager.getChangeDir(ctx.issue.number);
      const selfReviewReport = changeDir ? readReportFile(changeDir, 'self-review.md') : null;
      return {
        ...result,
        output: {
          stage: Stage.Plan,
          issueNumber: ctx.issue.number,
          selfReviewNotes: selfReviewReport,
        },
      };
    }

    return result;
  }
}

function emitRoundStart(
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

async function commitPlanArtifacts(changeDir: string, issue: { number: number; title: string }): Promise<boolean> {
  try {
    const worktreePath = path.dirname(path.dirname(path.dirname(changeDir)));
    const relPath = path.relative(worktreePath, changeDir);

    const { stdout: statusOut } = await execFileAsync(
      'git',
      ['status', '--porcelain', '--', relPath],
      { cwd: worktreePath },
    );

    if (!statusOut.trim()) {
      log.info('No uncommitted plan artifacts', { issueNumber: issue.number });
      return true;
    }

    await execFileAsync('git', ['add', '--', relPath], { cwd: worktreePath });
    await execFileAsync(
      'git',
      ['commit', '-m', `plan(issue-${issue.number}): ${issue.title}`, '--no-verify'],
      { cwd: worktreePath },
    );

    log.info('Plan artifacts committed', { issueNumber: issue.number, changeDir });
    return true;
  } catch (err) {
    log.warn('Failed to commit plan artifacts', {
      issueNumber: issue.number,
      error: err instanceof Error ? err.message : String(err),
    });
    return false;
  }
}
