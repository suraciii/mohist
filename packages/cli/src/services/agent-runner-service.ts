import type { IssueRepo } from '../db/issue-repo';
import type { ProjectRepo } from '../db/project-repo';
import type { CoderSessionRepo } from '../db/coder-session-repo';
import type { SessionStreamLogRepo } from '../db/session-stream-log-repo';
import type { WorkflowLogRepo } from '../db/workflow-log-repo';
import type { LlmConfig } from '../agent-runtime';
import type { AgentSessionOptions } from '../agent-runtime/agent-session';
import type { IssueTaskQueueRepo, IssueTaskQueueRecord, TaskType as QueueTaskType } from '../db/issue-task-queue-repo';
import {
  WorkflowEngine,
  type PipelineResult,
  GenericStageRunner,
} from '../workflow';
import { createCheckpointManager } from '../workflow/checkpoint-manager';
import { ChangeArtifactsManager } from '../artifacts/change-artifacts-manager';
import { IssueStatus, type Issue } from '../types';
import { EventBus } from './event-bus';
import { Stage, STAGE_TRANSITIONS } from '../types';
import { load } from '../config/config-loader';
import { maskSensitiveData } from '../utils/sensitive-data';
import { Log } from '../util/log';
import { PipelineCheckpointRepo } from '../db/pipeline-checkpoint-repo';
import { StageExecutionRepo } from '../db/stage-execution-repo';
import type { StageStateService } from './stage-state-service';
import type { WorkflowRunService } from './workflow-run-service';
import { WorkflowApplicationService } from './workflow-application-service';
import type { IssuePrerequisiteService } from './issue-prerequisite-service';
import { WorktreeManager } from '../git/worktree-manager';
import { isCurrentStageApproval } from '../workflow/issue-lifecycle';
import { createTaskLoaderRegistry, createRalphTaskLoader, createDefaultStaticTaskLoader } from '../workflow/tasks';
import { DEFAULT_STAGE_DEFINITIONS } from '../workflow/definition/default-workflow';
import { createDefaultCheckRegistry } from '../workflow/checks/default-check-registry';
export { createDefaultCheckRegistry } from '../workflow/checks/default-check-registry';
import { resolveWorkflowDefinition, validateWorkflowDefinition } from '../workflow/definition/workflow-inspector';
import { workflowDefinitionSnapshotFromUnknown } from '../workflow/projection/workflow-run-snapshot';

export type TaskType = QueueTaskType;

export interface EnqueueOptions {
  priority?: number;
}

export interface EnqueueResult {
  taskId: string;
  status: 'pending' | 'running';
  queuePosition?: number;
}

export interface IssueQueueStatus {
  running: IssueTaskQueueRecord | null;
  pending: IssueTaskQueueRecord[];
  queueLength: number;
}

export interface GlobalQueueStatus {
  totalRunning: number;
  totalPending: number;
  maxSlots: number;
  issues: Map<string, IssueQueueStatus>;
}

export interface RecoverableIssue {
  issueNumber: number;
  stage: string;
}

export interface BlockedIssueInfo {
  issueId: string;
  issueNumber: number;
  projectId: string;
  blockedReason?: string;
  retryCount: number;
  stage: string;
}

export interface AgentStatus {
  running: number;
  pending: number;
  maxSlots: number;
  tasks: Array<{ taskId: string; issueId: string; issueNumber: number; taskType: string; status: string }>;
  waitingQuestions: Array<{ issueId: string; issueNumber: number; projectId: string; questionId: string; question: string }>;
  recoverableIssues: RecoverableIssue[];
  blockedIssues: BlockedIssueInfo[];
}

export interface WaitingQuestion {
  questionId: string;
  question: string;
}

const PIPELINE_TIMEOUT_MS = 90 * 60 * 1000;

const log = Log.create({ service: 'agent-runner' });

export function isCurrentStageAwaitingApproval(issue: Issue | null | undefined): boolean {
  return Boolean(issue && isCurrentStageApproval(issue, issue.stage, 'awaiting'));
}

function isAwaitingApprovalForReachableStage(issue: Issue | null | undefined): boolean {
  if (!issue?.approvalState || issue.approvalState.status !== 'awaiting') return false;
  if (issue.approvalState.stage === issue.stage) return true;
  return STAGE_TRANSITIONS[issue.stage]?.includes(issue.approvalState.stage) ?? false;
}

export class AgentRunnerService {
  private waitingQuestions = new Map<string, WaitingQuestion>();
  private readonly maxConcurrentAgents: number;
  private recoverableIssues: RecoverableIssue[];
  private llmConfig?: LlmConfig;
  private readonly providersChangedListener: (data: { providers: Array<{ id: string; name?: string; apiKey?: string; baseURL?: string; sdk?: string; models?: string[] }> }) => void;
  private orphanScanTimer?: ReturnType<typeof setInterval>;
  private runningSlots = new Map<string, IssueTaskQueueRecord>();
  private pendingQueues = new Map<string, IssueTaskQueueRecord[]>();
  private abortControllers = new Map<string, AbortController>();
  private queueRecovered = false;

  constructor(
    private readonly eventBus: EventBus,
    private readonly workflowLogRepo?: WorkflowLogRepo,
    private readonly issueRepo?: IssueRepo,
    maxConcurrentAgents: number = 8,
    private readonly coderSessionRepo?: CoderSessionRepo,
    private readonly checkpointRepo?: PipelineCheckpointRepo,
    private readonly projectRepo?: ProjectRepo,
    private readonly worktreeManager?: WorktreeManager,
    private readonly taskQueueRepo?: IssueTaskQueueRepo,
    _conflictResolutionDeps?: unknown,
    private readonly sessionStreamLogRepo?: SessionStreamLogRepo,
    private readonly stageExecutionRepo?: StageExecutionRepo,
    private readonly stageStateService?: StageStateService,
    private readonly workflowRunService?: WorkflowRunService,
    private readonly issuePrerequisiteService?: IssuePrerequisiteService,
  ) {
    this.maxConcurrentAgents = maxConcurrentAgents;
    this.recoverableIssues = this.detectRecoverableIssues();
    log.info('AgentRunnerService initialized', { maxConcurrentAgents: this.maxConcurrentAgents });
    if (this.recoverableIssues.length > 0) {
      log.info('Detected recoverable issues', { count: this.recoverableIssues.length, issues: this.recoverableIssues.map(i => `#${i.issueNumber} (${i.stage})`).join(', ') });
    }

    this.providersChangedListener = (_data) => {
      this.handleProvidersChanged();
    };
    this.eventBus.on('config:providers:changed', this.providersChangedListener);

    this.orphanScanTimer = setInterval(() => this.scanOrphanedIssues(), 5 * 60 * 1000);
  }

  shutdown(): void {
    this.eventBus.off('config:providers:changed', this.providersChangedListener);

    if (this.orphanScanTimer) {
      clearInterval(this.orphanScanTimer);
      this.orphanScanTimer = undefined;
    }

    for (const [issueId, ac] of this.abortControllers) {
      try {
        ac.abort();
        log.info('Aborted agent during shutdown', { issueId });
      } catch (err) {
        log.error('Failed to abort agent during shutdown', {
          issueId,
          error: err instanceof Error ? err.message : String(err),
        });
      }
    }

    this.runningSlots.clear();
    this.pendingQueues.clear();
    this.abortControllers.clear();
    this.waitingQuestions.clear();
    log.info('AgentRunnerService shutdown complete, all maps cleared');
  }

  private handleProvidersChanged(): void {
    try {
      log.info('Provider config changed, reloading LLM config');
      const freshConfig = load();
      this.llmConfig = freshConfig;
      const maskedConfig = maskSensitiveData(freshConfig as unknown as Record<string, unknown>);
      log.info('LLM config reloaded successfully', { config: JSON.stringify(maskedConfig) });
    } catch (err) {
      log.error('Failed to reload LLM config', { error: err instanceof Error ? err.message : String(err) });
    }
  }

  setLlmConfig(config: LlmConfig): void {
    this.llmConfig = config;
  }

  getLlmConfig(): LlmConfig | undefined {
    return this.llmConfig;
  }

  forceStop(issueId: string): { stopped: boolean } {
    const ac = this.abortControllers.get(issueId);
    if (!ac) return { stopped: false };

    log.info('Force stopping agent', { issueId });
    ac.abort();

    this.abortControllers.delete(issueId);
    this.waitingQuestions.delete(issueId);

    this.cancelRunningCoderSessions(issueId);

    if (this.workflowRunService) {
      try {
        const was = new WorkflowApplicationService(this.workflowRunService.getDatabaseManager());
        was.interruptRunningWorkAttempts({ issueId, reason: 'Agent stopped by user', diagnostic: 'force_stop' });
      } catch (e) {
        log.warn('Failed to interrupt work attempts during force stop', {
          issueId,
          error: e instanceof Error ? e.message : String(e),
        });
      }
    }

    this.eventBus.emit('agent_stopped', {
      issueId,
      projectId: this.runningSlots.get(issueId)?.projectId ?? '',
      issueNumber: this.runningSlots.get(issueId)?.issueNumber ?? 0,
      reason: 'force_stop',
    });

    return { stopped: true };
  }

  private detectRecoverableIssues(): RecoverableIssue[] {
    if (!this.issueRepo) return [];
    const activeIssues = this.issueRepo.findAll({ status: IssueStatus.Active });
    return activeIssues
      .filter(issue => issue.stage !== Stage.Backlog)
      .map(issue => ({ issueNumber: issue.number, stage: issue.stage }));
  }

  recoverFromQueue(): void {
    if (!this.taskQueueRepo || !this.issueRepo) {
      log.info('Queue recovery skipped — missing taskQueueRepo or issueRepo');
      return;
    }
    if (this.queueRecovered) {
      log.info('Queue recovery skipped — already recovered in this service lifecycle');
      return;
    }
    this.queueRecovered = true;

    const runningTasks = this.taskQueueRepo.findAllRunning();
    log.info('Recovering queue state from DB', { runningTasks: runningTasks.length });

    for (const task of runningTasks) {
      const issue = this.issueRepo.findById(task.issueId);

      if (issue?.mergeState) {
        this.taskQueueRepo.updateStatus(task.id, 'failed', {
          result: 'Server restarted',
          completedAt: new Date().toISOString(),
        });
        if (issue) {
          this.issueRepo.updateStatus(issue.id, IssueStatus.Interrupted);
          this.cleanupOrphanedCoderSessions(issue.id, issue.number);
        }
        log.info('Recovered running task — merge in progress, marked failed', {
          taskId: task.id,
          issueNumber: task.issueNumber,
          action: 'task=failed, issue=interrupted',
        });
      } else if (!isCurrentStageApproval(issue!, issue!.stage, 'awaiting')) {
        this.taskQueueRepo.updateStatus(task.id, 'failed', {
          result: 'Server restarted',
          completedAt: new Date().toISOString(),
        });
        if (issue) {
          this.issueRepo.updateStatus(issue.id, IssueStatus.Interrupted);
          this.cleanupOrphanedCoderSessions(issue.id, issue.number);
        }
        log.info('Recovered running task — mid-execution, marked failed', {
          taskId: task.id,
          issueNumber: task.issueNumber,
          action: 'task=failed, issue=interrupted',
        });
      } else {
        this.taskQueueRepo.updateStatus(task.id, 'completed', {
          result: 'awaiting_approval',
          completedAt: new Date().toISOString(),
        });
        log.info('Recovered running task — at approval checkpoint, marked completed', {
          taskId: task.id,
          issueNumber: task.issueNumber,
          stage: issue!.stage,
        });
      }
    }

    const pendingTasks = this.taskQueueRepo.findAllPending();
    log.info('Loading pending tasks from DB', { pendingTasks: pendingTasks.length });

    for (const task of pendingTasks) {
      const queue = this.pendingQueues.get(task.issueId) ?? [];
      this.insertByPriority(queue, task);
      this.pendingQueues.set(task.issueId, queue);
    }

    this.schedule();
    log.info('Queue recovery complete', {
      recoveredRunning: runningTasks.length,
      recoveredPending: pendingTasks.length,
    });
  }

  recoverIssues(): void {
    this.recoverFromQueue();

    if (!this.issueRepo) return;

    const orphans = [
      ...this.issueRepo.findAll({ status: IssueStatus.Active }),
      ...this.issueRepo.findAll({ status: IssueStatus.Blocked }).filter(isAwaitingApprovalForReachableStage),
    ]
      .filter(issue => issue.stage !== Stage.Backlog);

    if (orphans.length === 0) return;

    for (const issue of orphans) {
      this.recoverSingleIssue(issue);
    }

    this.recoverableIssues = [];
  }

  private scanOrphanedIssues(): void {
    if (!this.issueRepo || !this.coderSessionRepo) return;

    const activeIssues = this.issueRepo.findAll({ status: IssueStatus.Active })
      .filter(issue => issue.stage !== Stage.Backlog);

    const runningSessionIssueIds = new Set<string>();
    try {
      const runningSessions = this.coderSessionRepo.findAllRunning();
      for (const session of runningSessions) {
        runningSessionIssueIds.add(session.issueId);
      }
    } catch (err) {
      log.error('Failed to query running coder_sessions during orphan scan', {
        error: err instanceof Error ? err.message : String(err),
      });
    }

    const orphans = activeIssues.filter(issue => {
      if (this.runningSlots.has(issue.id)) return false;
      if (runningSessionIssueIds.has(issue.id)) return false;
      if (issue.mergeState) return false;
      if (isCurrentStageAwaitingApproval(issue)) return false;
      return true;
    });

    if (orphans.length === 0) return;

    log.info('Orphan scan detected issues without active agent or pending approval', {
      count: orphans.length,
      issues: orphans.map(i => `#${i.number} (${i.stage})`).join(', '),
    });

    for (const issue of orphans) {
      this.cleanupOrphanedCoderSessions(issue.id, issue.number);
      try {
        this.issueRepo!.blockIssue(issue.id, '检测到 agent 已退出但状态未更新，自动恢复');
        log.info('Orphan scan blocked issue', { issueNumber: issue.number, stage: issue.stage });
      } catch (err) {
        log.error('Orphan scan failed to block issue', {
          issueNumber: issue.number,
          error: err instanceof Error ? err.message : String(err),
        });
      }
    }
  }

  recoverSingleIssueById(issueId: string): void {
    if (!this.issueRepo) return;
    const issue = this.issueRepo.findById(issueId);
    if (!issue) return;
    if (issue.stage === Stage.Backlog) return;
    if (issue.status !== IssueStatus.Active && !isAwaitingApprovalForReachableStage(issue)) return;
    this.recoverSingleIssue(issue);
  }

  private recoverSingleIssue(issue: Issue): void {
    if (!this.issueRepo) return;
    try {
      const activeRun = this.workflowRunService?.getActiveRunForIssue(issue.id);
      if (activeRun) {
        if (issue.stage !== activeRun.currentStage) {
          this.issueRepo.updateStage(issue.id, activeRun.currentStage);
        }
        if (issue.status !== IssueStatus.Active) {
          this.issueRepo.updateStatus(issue.id, IssueStatus.Active);
        }
        this.issueRepo.updateBlockedReason(issue.id, null);
        log.info('Recovered issue from active WorkflowRun aggregate state', {
          issueNumber: issue.number,
          stage: activeRun.currentStage,
          action: 'stage/status projected from WorkflowRun, task/check state preserved',
        });
        return;
      }

      if (issue.status === IssueStatus.Completed || issue.stage === Stage.Done) {
        log.info('Completed issue needs no recovery action', {
          issueNumber: issue.number,
          action: 'terminal issue preserved',
        });
        return;
      }

      if (isAwaitingApprovalForReachableStage(issue)) {
        if (issue.approvalState!.stage !== issue.stage) {
          this.issueRepo.updateStage(issue.id, issue.approvalState!.stage);
        }
        if (issue.status !== IssueStatus.Active) {
          this.issueRepo.updateStatus(issue.id, IssueStatus.Active);
        }
        this.issueRepo.updateBlockedReason(issue.id, null);
        log.info('Restored awaiting issue', {
          issueNumber: issue.number,
          stage: issue.approvalState!.stage ?? issue.stage,
          action: 'stage/status reconciled, awaiting approval preserved',
        });
      } else {
        this.issueRepo.updateStatus(issue.id, IssueStatus.Interrupted);
        this.issueRepo.updateBlockedReason(issue.id, null);
        this.issueRepo.clearApprovalState(issue.id);
        this.cleanupOrphanedCoderSessions(issue.id, issue.number);
        log.info('Recovered orphaned issue', {
          issueNumber: issue.number,
          stage: issue.stage,
          action: 'status=interrupted, no active WorkflowRun to resume',
        });
      }
    } catch (err) {
      log.error('Failed to recover orphaned issue', {
        issueNumber: issue.number,
        error: err instanceof Error ? err.message : String(err),
      });
    }
  }

  private cleanupOrphanedCoderSessions(issueId: string, issueNumber: number): void {
    if (!this.coderSessionRepo) return;

    try {
      const sessions = this.coderSessionRepo.findByIssueId(issueId);
      const runningSessions = sessions.filter(s => s.status === 'running');

      for (const session of runningSessions) {
        try {
          this.coderSessionRepo.updateStatus(session.id, 'failed');
          log.info('Cleaned up orphaned coder_session', {
            issueNumber,
            sessionId: session.id,
            action: 'status=running→failed',
          });
        } catch (updateErr) {
          log.error('Failed to clean up orphaned coder_session', {
            issueNumber,
            sessionId: session.id,
            error: updateErr instanceof Error ? updateErr.message : String(updateErr),
          });
        }
      }

      if (runningSessions.length > 0) {
        log.info('Cleaned up orphaned coder_sessions for interrupted issue', {
          issueNumber,
          count: runningSessions.length,
        });
      }
    } catch (err) {
      log.error('Failed to query coder_sessions for cleanup', {
        issueNumber,
        error: err instanceof Error ? err.message : String(err),
      });
    }
  }

  private cancelRunningCoderSessions(issueId: string): void {
    if (!this.coderSessionRepo) return;
    try {
      const cancelled = this.coderSessionRepo.cancelRunningByIssueId(issueId, 'Agent stopped by user');
      if (cancelled > 0) {
        log.info('Cancelled running coder_sessions during force stop', { issueId, count: cancelled });
      }
    } catch (err) {
      log.error('Failed to cancel running coder_sessions during force stop', {
        issueId,
        error: err instanceof Error ? err.message : String(err),
      });
    }
  }

  setWaiting(issueId: string, questionId: string, question: string): void {
    this.waitingQuestions.set(issueId, { questionId, question });
  }

  clearWaiting(issueId: string): void {
    this.waitingQuestions.delete(issueId);
  }

  enqueue(
    issueId: string,
    taskType: TaskType,
    payload: Record<string, unknown> = {},
    options?: EnqueueOptions,
  ): EnqueueResult {
    if (!this.issueRepo || !this.taskQueueRepo) {
      throw new Error('IssueTaskQueueRepo and IssueRepo are required for queue operations');
    }

    const issue = this.issueRepo.findById(issueId);
    if (!issue) {
      throw new Error(`Issue not found: ${issueId}`);
    }

    const record = this.taskQueueRepo.insert({
      issueId: issue.id,
      issueNumber: issue.number,
      projectId: issue.projectId,
      taskType,
      payload: JSON.stringify(payload),
      priority: options?.priority ?? 0,
    });

    const queue = this.pendingQueues.get(issueId) ?? [];
    this.insertByPriority(queue, record);
    this.pendingQueues.set(issueId, queue);

    log.info('Task enqueued', {
      taskId: record.id,
      issueNumber: issue.number,
      taskType,
      priority: record.priority,
      queuePosition: queue.indexOf(record),
    });

    const immediateStart = this.runningSlots.size < this.maxConcurrentAgents
      && !this.runningSlots.has(issueId);

    if (immediateStart) {
      this.schedule();
    }

    const updatedRecord = this.taskQueueRepo.findById(record.id);
    const started = updatedRecord?.status === 'running';
    const position = started ? undefined : queue.findIndex(t => t.id === record.id);

    return {
      taskId: record.id,
      status: started ? 'running' : 'pending',
      queuePosition: position !== undefined && position >= 0 ? position : undefined,
    };
  }

  cancel(taskId: string): boolean {
    if (!this.taskQueueRepo) return false;

    const record = this.taskQueueRepo.findById(taskId);
    if (!record) return false;

    if (record.status === 'running') {
      return false;
    }

    if (record.status !== 'pending') {
      return false;
    }

    this.taskQueueRepo.updateStatus(taskId, 'cancelled', {
      result: 'cancelled',
      completedAt: new Date().toISOString(),
    });

    const queue = this.pendingQueues.get(record.issueId);
    if (queue) {
      const idx = queue.findIndex(t => t.id === taskId);
      if (idx >= 0) {
        queue.splice(idx, 1);
      }
      if (queue.length === 0) {
        this.pendingQueues.delete(record.issueId);
      }
    }

    log.info('Task cancelled', { taskId, issueId: record.issueId });
    return true;
  }

  cancelAll(issueId: string): void {
    if (!this.taskQueueRepo) return;

    const queue = this.pendingQueues.get(issueId);
    if (queue) {
      for (const task of queue) {
        this.taskQueueRepo.updateStatus(task.id, 'cancelled', {
          result: 'cancelled',
          completedAt: new Date().toISOString(),
        });
      }
      this.pendingQueues.delete(issueId);
    }

    this.taskQueueRepo.cancelPendingByIssueId(issueId);

    const runningTask = this.runningSlots.get(issueId);
    if (runningTask) {
      this.forceStop(issueId);
      this.runningSlots.delete(issueId);

      this.taskQueueRepo.updateStatus(runningTask.id, 'cancelled', {
        result: 'cancelled',
        completedAt: new Date().toISOString(),
      });

      this.schedule();
    }

    log.info('All tasks cancelled for issue', { issueId });
  }

  getQueueStatus(issueId?: string): IssueQueueStatus | GlobalQueueStatus {
    if (issueId !== undefined) {
      const running = this.runningSlots.get(issueId) ?? null;
      const pending = this.pendingQueues.get(issueId) ?? [];
      return {
        running,
        pending: [...pending],
        queueLength: pending.length,
      };
    }

    const issues = new Map<string, IssueQueueStatus>();
    for (const [id, task] of this.runningSlots) {
      issues.set(id, {
        running: task,
        pending: [...(this.pendingQueues.get(id) ?? [])],
        queueLength: this.pendingQueues.get(id)?.length ?? 0,
      });
    }
    for (const [id, queue] of this.pendingQueues) {
      if (!issues.has(id)) {
        issues.set(id, { running: null, pending: [...queue], queueLength: queue.length });
      }
    }

    let totalPending = 0;
    for (const queue of this.pendingQueues.values()) {
      totalPending += queue.length;
    }

    return {
      totalRunning: this.runningSlots.size,
      totalPending,
      maxSlots: this.maxConcurrentAgents,
      issues,
    };
  }

  private insertByPriority(queue: IssueTaskQueueRecord[], task: IssueTaskQueueRecord): void {
    let insertIdx = queue.length;
    for (let i = 0; i < queue.length; i++) {
      if (task.priority > queue[i].priority) {
        insertIdx = i;
        break;
      }
    }
    queue.splice(insertIdx, 0, task);
  }

  schedule(): void {
    if (!this.taskQueueRepo) return;

    while (this.runningSlots.size < this.maxConcurrentAgents) {
      const candidate = this.pickHighestPriorityPending();
      if (!candidate) break;

      const queue = this.pendingQueues.get(candidate.issueId);
      if (queue) {
        const idx = queue.findIndex(t => t.id === candidate.id);
        if (idx >= 0) {
          queue.splice(idx, 1);
        }
        if (queue.length === 0) {
          this.pendingQueues.delete(candidate.issueId);
        }
      }

      const now = new Date().toISOString();
      this.taskQueueRepo.updateStatus(candidate.id, 'running', { startedAt: now });

      const runningRecord = this.taskQueueRepo.findById(candidate.id) ?? candidate;
      this.runningSlots.set(candidate.issueId, {
        ...runningRecord,
        status: 'running',
        startedAt: runningRecord.startedAt ?? now,
      });

      log.info('Task started', {
        taskId: candidate.id,
        issueId: candidate.issueId,
        issueNumber: candidate.issueNumber,
        taskType: candidate.taskType,
        slotsUsed: this.runningSlots.size,
      });

      this.executeTask(candidate);
    }
  }

  private pickHighestPriorityPending(): IssueTaskQueueRecord | null {
    let best: IssueTaskQueueRecord | null = null;

    for (const [issueId, queue] of this.pendingQueues) {
      if (queue.length === 0) continue;
      if (this.runningSlots.has(issueId)) continue;

      const front = queue[0];
      if (!best) {
        best = front;
        continue;
      }

      if (front.priority > best.priority) {
        best = front;
      } else if (front.priority === best.priority && front.enqueuedAt < best.enqueuedAt) {
        best = front;
      }
    }

    return best;
  }

  private executeTask(task: IssueTaskQueueRecord): void {
    if (!this.issueRepo || !this.taskQueueRepo) return;

    const taskPromise = (async () => {
      try {
        const issue = this.issueRepo!.findById(task.issueId);
        if (!issue) {
          this.completeTask(task.id, 'completed', 'skipped');
          return;
        }

        switch (task.taskType) {
          case 'start-pipeline':
            await this.executeStartPipelineTask(task, issue);
            break;
          case 'resume-pipeline':
            await this.executeResumePipelineTask(task, issue);
            break;
          default:
            this.completeTask(task.id, 'completed', 'skipped');
        }
      } catch (err) {
        log.error('Task execution failed', {
          taskId: task.id,
          taskType: task.taskType,
          issueId: task.issueId,
          error: err instanceof Error ? err.message : String(err),
        });
        this.completeTask(task.id, 'failed', err instanceof Error ? err.message : String(err));
      }
    })();

    taskPromise.catch(() => {});
  }

  private async executeStartPipelineTask(task: IssueTaskQueueRecord, issue: Issue): Promise<void> {
    if (!this.issueRepo || !this.projectRepo || !this.worktreeManager) {
      this.completeTask(task.id, 'failed', 'Missing dependencies for pipeline execution');
      return;
    }

    if (this.issuePrerequisiteService) {
      const eligibility = this.issuePrerequisiteService.evaluateStartEligibility(issue);
      if (!eligibility.startable) {
        log.info('Skipping start-pipeline: issue is not start eligible', {
          issueNumber: issue.number,
          reason: eligibility.reason,
          message: eligibility.message,
        });
        this.completeTask(task.id, 'completed', `skipped: ${eligibility.message ?? eligibility.reason}`);
        return;
      }
    } else {
      if (issue.status === IssueStatus.Blocked) {
        log.info('Skipping start-pipeline: issue is blocked', { issueNumber: issue.number });
        this.completeTask(task.id, 'completed', 'skipped');
        return;
      }

      if (issue.stage !== Stage.Backlog) {
        log.info('Skipping start-pipeline: issue not in backlog', { issueNumber: issue.number, stage: issue.stage });
        this.completeTask(task.id, 'completed', 'skipped');
        return;
      }
    }

    const project = this.projectRepo.findById(issue.projectId);
    if (!project) {
      this.completeTask(task.id, 'failed', `Project not found: ${issue.projectId}`);
      return;
    }

    const worktreePath = await this.ensureWorktree(project.path, project.name, issue.number, project.baseBranch);
    if (!worktreePath) {
      this.completeTask(task.id, 'failed', `Failed to create worktree for issue #${issue.number}`);
      return;
    }

    if (this.workflowRunService) {
      try {
        const resolvedWorkflow = resolveWorkflowDefinition(worktreePath);
        const diagnostics = validateWorkflowDefinition(resolvedWorkflow);
        const blockingDiagnostics = diagnostics.filter(diagnostic => diagnostic.severity === 'error');
        if (blockingDiagnostics.length > 0) {
          const message = blockingDiagnostics.map(diagnostic => `${diagnostic.path}: ${diagnostic.message}`).join('; ');
          this.completeTask(task.id, 'failed', `Workflow definition is invalid: ${message}`);
          return;
        }
        this.workflowRunService.startRun(issue.id, issue.number, 'start-pipeline', resolvedWorkflow.snapshot);
        log.info('WorkflowRun started for issue', { issueNumber: issue.number });
      } catch (err) {
        log.warn('Failed to start WorkflowRun', { issueNumber: issue.number, error: err instanceof Error ? err.message : String(err) });
      }
    }

    const acpOptions: AgentSessionOptions = { cwd: worktreePath };
    await this.runPipelineToCompletion(task, issue, issue.projectId, this.issueRepo, worktreePath, acpOptions);
  }

  private async executeResumePipelineTask(task: IssueTaskQueueRecord, issue: Issue): Promise<void> {
    if (!this.issueRepo || !this.projectRepo || !this.worktreeManager) {
      this.completeTask(task.id, 'failed', 'Missing dependencies for pipeline execution');
      return;
    }

    if (issue.status === IssueStatus.Blocked) {
      if (isCurrentStageApproval(issue, issue.stage, 'approved')) {
        log.info('Unblocking approved issue before resume', { issueNumber: issue.number });
        this.issueRepo.updateStatus(issue.id, IssueStatus.Active);
        this.issueRepo.updateBlockedReason(issue.id, null);
      } else if (this.workflowRunService) {
        if (this.canResumeBlockedIssue(issue)) {
          log.info('Allowing resumable blocked issue to resume pipeline', { issueNumber: issue.number });
        } else if (this.canRetryBlockedIssue(issue)) {
          log.info('Allowing retryable blocked issue to resume pipeline', { issueNumber: issue.number });
        } else {
          log.info('Skipping resume-pipeline: issue is blocked without retryable current-stage failure', { issueNumber: issue.number });
          this.completeTask(task.id, 'completed', 'skipped');
          return;
        }
      } else {
        log.info('Skipping resume-pipeline: issue is blocked', { issueNumber: issue.number });
        this.completeTask(task.id, 'completed', 'skipped');
        return;
      }
    }

    if (issue.status === IssueStatus.Completed || issue.stage === Stage.Done) {
      log.info('Skipping resume-pipeline: issue already done', { issueNumber: issue.number });
      this.completeTask(task.id, 'completed', 'skipped');
      return;
    }

    const project = this.projectRepo.findById(issue.projectId);
    if (!project) {
      this.completeTask(task.id, 'failed', `Project not found: ${issue.projectId}`);
      return;
    }

    const worktreePath = this.worktreeManager.getPath(project.name, issue.number);
    if (!worktreePath) {
      this.completeTask(task.id, 'failed', `Worktree not found for issue #${issue.number}`);
      return;
    }

    const acpOptions: AgentSessionOptions = { cwd: worktreePath };
    await this.runPipelineToCompletion(task, issue, issue.projectId, this.issueRepo, worktreePath, acpOptions);
  }

  private canResumeBlockedIssue(issue: Issue): boolean {
    if (issue.status !== IssueStatus.Blocked) return false;
    if (!this.workflowRunService) return false;
    try {
      const recovery = new WorkflowApplicationService(this.workflowRunService.getDatabaseManager()).getRecoveryProjection(issue.id);
      return recovery?.latestAttemptState === 'interrupted' && recovery.allowedActions.includes('resume');
    } catch (error) {
      log.warn('Failed to evaluate blocked issue recovery projection', {
        issueNumber: issue.number,
        error: error instanceof Error ? error.message : String(error),
      });
      return false;
    }
  }

  private canRetryBlockedIssue(issue: Issue): boolean {
    if (issue.status !== IssueStatus.Blocked) return false;
    if (!this.workflowRunService) return false;

    try {
      const availability = new WorkflowApplicationService(this.workflowRunService.getDatabaseManager()).checkRetryAvailability({
        issueId: issue.id,
        stage: issue.stage,
      });
      return availability.available;
    } catch (error) {
      log.warn('Failed to evaluate blocked issue retry availability', {
        issueNumber: issue.number,
        error: error instanceof Error ? error.message : String(error),
      });
      return false;
    }
  }

  private async runPipelineToCompletion(
    task: IssueTaskQueueRecord,
    issue: Issue,
    projectId: string,
    issueRepo: IssueRepo,
    worktreePath: string,
    acpOptions: AgentSessionOptions,
  ): Promise<void> {
    this.eventBus.emit('agent_started', { issueId: issue.id, projectId });
    log.info('Pipeline started via task queue', { issueNumber: issue.number, taskType: task.taskType, taskId: task.id });

    const abortController = new AbortController();
    this.abortControllers.set(issue.id, abortController);
    const startTime = Date.now();

    try {
      try {
        issueRepo.updateStatus(issue.id, IssueStatus.Active);
      } catch (statusErr) {
        log.warn('Failed to set issue status to active before pipeline', {
          issueNumber: issue.number,
          error: statusErr instanceof Error ? statusErr.message : String(statusErr),
        });
      }

      const artifactManager = new ChangeArtifactsManager(worktreePath);
      const checkpointManager = this.checkpointRepo
        ? createCheckpointManager(this.checkpointRepo)
        : createCheckpointManager({ get: () => null, upsert: () => {}, delete: () => {} } as any);

      const taskLoaderRegistry = createTaskLoaderRegistry([
        createDefaultStaticTaskLoader(worktreePath),
        createRalphTaskLoader(),
      ]);

      const activeWorkflowRun = this.workflowRunService?.getActiveRunForIssue(issue.id);
      const workflowDefinitionSnapshot = workflowDefinitionSnapshotFromUnknown(activeWorkflowRun?.workflowDefinition) ?? undefined;
      const checkRegistry = createDefaultCheckRegistry({ worktreePath, workflowDefinitionSnapshot });

      const unifiedRunner = new GenericStageRunner({
        taskLoaderRegistry,
        checkRegistry,
        getStageDefinition: (stage) => {
          const activeRun = this.workflowRunService?.getActiveRunForIssue(issue.id);
          const snapshot = workflowDefinitionSnapshotFromUnknown(activeRun?.workflowDefinition);
          return snapshot?.compiledStageDefinitions.find(d => d.stage === stage)
            ?? DEFAULT_STAGE_DEFINITIONS.find(d => d.stage === stage);
        },
        worktreePath,
      });

      const runners = [unifiedRunner];
      const workflowApplicationService = this.workflowRunService
        ? new WorkflowApplicationService(this.workflowRunService.getDatabaseManager())
        : undefined;
      const pipeline = new WorkflowEngine({
        runners,
        artifactManager,
        issueRepo,
        eventBus: this.eventBus,
        projectId,
        checkpointManager,
        worktreeManager: this.worktreeManager,
        projectRepo: this.projectRepo,
        signal: abortController.signal,
        coderSessionRepo: this.coderSessionRepo,
        workflowLogRepo: this.workflowLogRepo,
        sessionStreamLogRepo: this.sessionStreamLogRepo,
        stageExecutionRepo: this.stageExecutionRepo,
        stageStateService: this.stageStateService,
        workflowRunService: this.workflowRunService,
        workflowApplicationService,
        config: load(),
      });

      const abortPromise = new Promise<never>((_resolve, reject) => {
        abortController.signal.addEventListener('abort', () => {
          reject(new Error('Agent stopped by user'));
        });
      });

      let timeoutId: ReturnType<typeof setTimeout> | undefined;
      const timeoutPromise = new Promise<never>((_resolve, reject) => {
        timeoutId = setTimeout(() => {
          abortController.abort();
          reject(new Error(`Pipeline timed out after ${PIPELINE_TIMEOUT_MS / 60000} minutes`));
        }, PIPELINE_TIMEOUT_MS);
      });

      let result: PipelineResult;
      try {
        result = await Promise.race([
          pipeline.run(issue, acpOptions),
          abortPromise,
          timeoutPromise,
        ]);
      } finally {
        if (timeoutId !== undefined) clearTimeout(timeoutId);
      }

      const duration = Date.now() - startTime;
      const latestIssue = issueRepo.findById(issue.id);
      const isPaused = !result.completed && isCurrentStageAwaitingApproval(latestIssue);
      log.info('Pipeline run completed', { issueNumber: issue.number, elapsedMs: duration, completed: result.completed, paused: isPaused });

      if (isPaused) {
        this.eventBus.emit('agent_paused', {
          issueId: issue.id,
          projectId,
          issueNumber: issue.number,
        });
        log.info('Pipeline paused at approval, marking task completed', {
          issueNumber: issue.number,
          stage: result.stage,
          taskId: task.id,
        });
        this.completeTask(task.id, 'completed', 'awaiting_approval');
        return;
      }

      if (result.completed) {
        this.eventBus.emit('agent_completed', { issueId: issue.id, projectId, issueNumber: issue.number });
        this.completeTask(task.id, 'completed', 'success');
      } else {
        const failureSummary = result.stage
          ? `[${result.stage}] ${result.message ?? 'Pipeline 未完成'}`
          : result.message ?? 'Pipeline 未完成';
        this.handlePipelineFailure(issue, issueRepo, projectId, failureSummary);
        this.completeTask(task.id, 'failed', failureSummary);
      }
    } catch (err) {
      const rawErrorMsg = err instanceof Error ? err.message : String(err);
      const currentIssue = issueRepo.findById(issue.id);
      const stageLabel = currentIssue?.stage ?? 'unknown';
      const errorMsg = `[${stageLabel}] Pipeline 异常: ${rawErrorMsg}`;
      log.error('Pipeline execution failed', {
        issueNumber: issue.number,
        stage: stageLabel,
        error: rawErrorMsg,
      });
      this.handlePipelineFailure(issue, issueRepo, projectId, errorMsg);
      this.completeTask(task.id, 'failed', errorMsg);
    } finally {
      this.abortControllers.delete(issue.id);
      this.clearWaiting(issue.id);
    }
  }

  private handlePipelineFailure(issue: Issue, issueRepo: IssueRepo, projectId: string, errorMsg: string): void {
    try {
      issueRepo.blockIssue(issue.id, errorMsg);
    } catch (updateErr) {
      log.error('Failed to block issue', {
        issueNumber: issue.number,
        error: updateErr instanceof Error ? updateErr.message : String(updateErr),
      });
    }
    try {
      this.eventBus.emit('agent_error', { issueId: issue.id, projectId, error: errorMsg });
    } catch (emitErr) {
      log.error('Failed to emit agent_error event', {
        issueNumber: issue.number,
        error: emitErr instanceof Error ? emitErr.message : String(emitErr),
      });
    }
  }

  private async ensureWorktree(projectPath: string, projectName: string, issueNumber: number, baseBranch: string): Promise<string | null> {
    if (!this.worktreeManager) return null;
    try {
      if (this.worktreeManager.exists(projectName, issueNumber)) {
        return this.worktreeManager.getPath(projectName, issueNumber);
      }
      return await this.worktreeManager.create(projectPath, projectName, issueNumber, baseBranch);
    } catch (err) {
      log.error('Failed to ensure worktree', { issueNumber, error: err instanceof Error ? err.message : String(err) });
      return null;
    }
  }

  private completeTask(taskId: string, status: 'completed' | 'failed', result: string): void {
    if (!this.taskQueueRepo) return;

    this.taskQueueRepo.updateStatus(taskId, status, {
      result,
      completedAt: new Date().toISOString(),
    });

    for (const [issueId, running] of this.runningSlots) {
      if (running.id === taskId) {
        this.runningSlots.delete(issueId);
        this.abortControllers.delete(issueId);
        log.info('Task completed, slot released', { taskId, issueId, status });
        break;
      }
    }

    this.schedule();
  }

  getWaitingQuestions(): Map<string, WaitingQuestion> {
    return this.waitingQuestions;
  }

  isIssueAwaitingApproval(issueId: string): boolean {
    if (!this.issueRepo) return false;
    const issue = this.issueRepo.findById(issueId);
    if (!issue) return false;
    return isCurrentStageApproval(issue, issue.stage, 'awaiting');
  }

  getBlockedIssues(): BlockedIssueInfo[] {
    if (!this.issueRepo) return [];
    const blocked = this.issueRepo.findAll({ status: IssueStatus.Blocked });
    return blocked.map(issue => ({
      issueId: issue.id,
      issueNumber: issue.number,
      projectId: issue.projectId,
      blockedReason: issue.blockedReason,
      retryCount: issue.retryCount ?? 0,
      stage: issue.stage,
    }));
  }

  getStatus(): AgentStatus {
    const tasks: Array<{ taskId: string; issueId: string; issueNumber: number; taskType: string; status: string }> = [];
    for (const [issueId, record] of this.runningSlots) {
      tasks.push({
        taskId: record.id,
        issueId,
        issueNumber: record.issueNumber,
        taskType: record.taskType,
        status: 'running',
      });
    }
    for (const [, queue] of this.pendingQueues) {
      for (const record of queue) {
        tasks.push({
          taskId: record.id,
          issueId: record.issueId,
          issueNumber: record.issueNumber,
          taskType: record.taskType,
          status: 'pending',
        });
      }
    }

    const waiting = Array.from(this.waitingQuestions.entries()).map(([issueId, wq]) => {
      const slot = this.runningSlots.get(issueId);
      return {
        issueId,
        issueNumber: slot?.issueNumber ?? 0,
        projectId: slot?.projectId ?? '',
        questionId: wq.questionId,
        question: wq.question,
      };
    });

    let totalPending = 0;
    for (const queue of this.pendingQueues.values()) {
      totalPending += queue.length;
    }

    return {
      running: this.runningSlots.size,
      pending: totalPending,
      maxSlots: this.maxConcurrentAgents,
      tasks,
      waitingQuestions: waiting,
      recoverableIssues: this.recoverableIssues,
      blockedIssues: this.getBlockedIssues(),
    };
  }
}
