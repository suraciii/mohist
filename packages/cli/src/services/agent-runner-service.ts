import type { IssueRepo } from '../db/issue-repo';
import type { ProjectRepo } from '../db/project-repo';
import type { CoderSessionRepo } from '../db/coder-session-repo';
import type { LlmConfig } from '../agent-runtime';
import type { AcpConnectionOptions } from '../agent-runtime/acp-session';
import type { IssueTaskQueueRepo, IssueTaskQueueRecord, TaskType as QueueTaskType } from '../db/issue-task-queue-repo';
import { WorkflowEngine, type PipelineResult, PlanStageRunner, BuildStageRunner, CheckStageRunner, BuildTestCheck, AiReviewCheck } from '../workflow';
import { createCheckpointManager } from '../workflow/checkpoint-manager';
import { ChangeArtifactsManager } from '../artifacts/change-artifacts-manager';
import { IssueStatus, type Issue } from '../types';
import { EventBus } from './event-bus';
import { Stage } from '../types';
import { load } from '../config/config-loader';
import { maskSensitiveData } from '../utils/sensitive-data';
import { Log } from '../util/log';
import { PipelineCheckpointRepo } from '../db/pipeline-checkpoint-repo';
import { findChangeDir } from '../openspec/detector';
import { WorktreeManager, smartFetch } from '../git/worktree-manager';
import { resolveConflictsViaAgent, type ConflictResolutionDeps } from './conflict-resolution';
import { execFile } from 'child_process';
import { promisify } from 'util';
import * as fs from 'fs';
import * as path from 'path';

const execFileAsync = promisify(execFile);

const REBASE_ALLOWED_STAGES: Stage[] = [Stage.Plan, Stage.Build, Stage.Check, Stage.Done];

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

const PIPELINE_TIMEOUT_MS = 30 * 60 * 1000;

const log = Log.create({ service: 'agent-runner' });

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

  constructor(
    private readonly eventBus: EventBus,
    _workflowLogRepo?: unknown,
    private readonly issueRepo?: IssueRepo,
    maxConcurrentAgents: number = 8,
    _agentSessionMessageRepo?: unknown,
    private readonly coderSessionRepo?: CoderSessionRepo,
    private readonly checkpointRepo?: PipelineCheckpointRepo,
    private readonly projectRepo?: ProjectRepo,
    private readonly worktreeManager?: WorktreeManager,
    private readonly taskQueueRepo?: IssueTaskQueueRepo,
    private readonly conflictResolutionDeps?: ConflictResolutionDeps,
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
      .filter(issue => issue.stage !== Stage.Draft && issue.stage !== Stage.Backlog)
      .map(issue => ({ issueNumber: issue.number, stage: issue.stage }));
  }

  recoverFromQueue(): void {
    if (!this.taskQueueRepo || !this.issueRepo) {
      log.info('Queue recovery skipped — missing taskQueueRepo or issueRepo');
      return;
    }

    const runningTasks = this.taskQueueRepo.findAllRunning();
    log.info('Recovering queue state from DB', { runningTasks: runningTasks.length });

    for (const task of runningTasks) {
      const issue = this.issueRepo.findById(task.issueId);

      if (issue?.approvalState?.status === 'awaiting') {
        this.taskQueueRepo.updateStatus(task.id, 'completed', {
          result: 'approval_gate',
          completedAt: new Date().toISOString(),
        });
        log.info('Recovered running task — at approval gate, marked completed', {
          taskId: task.id,
          issueNumber: task.issueNumber,
          stage: issue.stage,
        });
      } else {
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

    const orphans = this.issueRepo.findAll({ status: IssueStatus.Active })
      .filter(issue => issue.stage !== Stage.Draft && issue.stage !== Stage.Backlog);

    if (orphans.length === 0) return;

    for (const issue of orphans) {
      this.recoverSingleIssue(issue);
    }

    this.recoverableIssues = [];
  }

  private scanOrphanedIssues(): void {
    if (!this.issueRepo || !this.coderSessionRepo) return;

    const activeIssues = this.issueRepo.findAll({ status: IssueStatus.Active })
      .filter(issue => issue.stage !== Stage.Draft && issue.stage !== Stage.Backlog);

    const orphans = activeIssues.filter(issue => {
      if (this.runningSlots.has(issue.id)) return false;
      if (issue.mergeState) return false;
      if (issue.approvalState?.status === 'awaiting') return false;
      return true;
    });

    if (orphans.length === 0) return;

    log.info('Orphan scan detected issues without active agent or pending gate', {
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
    if (issue.stage === Stage.Draft || issue.stage === Stage.Backlog) return;
    if (issue.status !== IssueStatus.Active) return;
    this.recoverSingleIssue(issue);
  }

  private recoverSingleIssue(issue: Issue): void {
    if (!this.issueRepo) return;
    try {
      if (issue.approvalState?.status === 'awaiting') {
        log.info('Restored awaiting issue', {
          issueNumber: issue.number,
          stage: issue.approvalState.stage ?? issue.stage,
          action: 'status remains active',
        });
      } else if (issue.stage === Stage.Check || issue.stage === Stage.Plan) {
        this.issueRepo.setApprovalState(issue.id, {
          stage: issue.stage,
          status: 'awaiting',
          output: { recovered: true, reason: `reopened at ${issue.stage} stage, restored to awaiting approval` },
          requestedAt: new Date().toISOString(),
        });
        log.info('Recovered review-stage issue', {
          issueNumber: issue.number,
          stage: issue.stage,
          action: 'status=active, approval restored',
        });
      } else if (issue.stage === Stage.Build && this.projectRepo && this.worktreeManager) {
        this.recoverBuildStageIssue(issue);
      } else {
        this.issueRepo.updateStatus(issue.id, IssueStatus.Interrupted);
        this.cleanupOrphanedCoderSessions(issue.id, issue.number);
        log.info('Recovered orphaned issue', {
          issueNumber: issue.number,
          stage: issue.stage,
          action: 'status=interrupted, stage preserved, checkpoint preserved',
        });
      }
    } catch (err) {
      log.error('Failed to recover orphaned issue', {
        issueNumber: issue.number,
        error: err instanceof Error ? err.message : String(err),
      });
    }
  }

  private recoverBuildStageIssue(issue: Issue): void {
    const MAX_RETRIES = 3;

    const project = this.projectRepo!.findById(issue.projectId);
    if (!project) {
      const reason = `Project 已不存在 (projectId: ${issue.projectId})`;
      this.issueRepo!.blockIssue(issue.id, reason);
      this.issueRepo!.clearApprovalState(issue.id);
      this.emitBlocked(issue, 'Project 已不存在', issue.retryCount ?? 0);

      log.info('Recovered build-stage orphan — project not found', {
        issueNumber: issue.number,
        action: 'status=blocked, project lookup failed',
      });
      return;
    }

    const worktreePath = this.worktreeManager!.getPath(project.name, issue.number);
    if (!worktreePath) {
      const reason = `Worktree 已不存在 (project: ${project.name}, issue: #${issue.number})`;
      this.issueRepo!.blockIssue(issue.id, reason);
      this.issueRepo!.clearApprovalState(issue.id);
      this.emitBlocked(issue, 'Worktree 已不存在', issue.retryCount ?? 0);

      log.info('Recovered build-stage orphan — no worktree found', {
        issueNumber: issue.number,
        action: 'status=blocked, worktree not found',
      });
      return;
    }

    const changeDir = findChangeDir(worktreePath, issue.number);
    if (!changeDir) {
      const reason = `变更目录不存在 (issue: #${issue.number})`;
      this.issueRepo!.blockIssue(issue.id, reason);
      this.issueRepo!.clearApprovalState(issue.id);
      this.emitBlocked(issue, '变更目录不存在', issue.retryCount ?? 0);
      log.info('Recovered build-stage orphan — missing change directory', {
        issueNumber: issue.number,
        action: 'status=blocked, no change directory found',
      });
      return;
    }

    const tasksPath = path.join(changeDir, 'tasks.json');
    if (!fs.existsSync(tasksPath)) {
      const reason = `tasks.json 不存在 (${tasksPath})`;
      this.issueRepo!.blockIssue(issue.id, reason);
      this.issueRepo!.clearApprovalState(issue.id);
      this.emitBlocked(issue, 'tasks.json 不存在', issue.retryCount ?? 0);

      log.info('Recovered build-stage orphan — missing tasks.json', {
        issueNumber: issue.number,
        action: 'status=blocked, change directory exists but no tasks.json',
      });
      return;
    }

    let tasksFile: { version: number; tasks: Array<{ id: string; passes: boolean }> };
    try {
      const raw = fs.readFileSync(tasksPath, 'utf-8');
      tasksFile = JSON.parse(raw);
    } catch {
      const reason = `tasks.json 格式损坏 (${tasksPath})`;
      this.issueRepo!.blockIssue(issue.id, reason);
      this.issueRepo!.clearApprovalState(issue.id);
      this.emitBlocked(issue, 'tasks.json 格式损坏', issue.retryCount ?? 0);

      log.info('Recovered build-stage orphan — malformed tasks.json', {
        issueNumber: issue.number,
        action: 'status=blocked, tasks.json parse failed',
      });
      return;
    }

    if (!tasksFile.tasks || !Array.isArray(tasksFile.tasks)) {
      const reason = `tasks.json 缺少 tasks 数组 (${tasksPath})`;
      this.issueRepo!.blockIssue(issue.id, reason);
      this.issueRepo!.clearApprovalState(issue.id);
      this.emitBlocked(issue, 'tasks.json 缺少 tasks 数组', issue.retryCount ?? 0);

      log.info('Recovered build-stage orphan — malformed tasks.json', {
        issueNumber: issue.number,
        action: 'status=blocked, tasks.json missing tasks array',
      });
      return;
    }

    const allPass = tasksFile.tasks.every(t => t.passes === true);
    if (allPass) {
      this.issueRepo!.updateStage(issue.id, Stage.Check);
      this.issueRepo!.setApprovalState(issue.id, {
        stage: Stage.Check,
        status: 'awaiting',
        output: { recovered: true, reason: 'build completed, auto-advanced to check' },
        requestedAt: new Date().toISOString(),
      });
      this.issueRepo!.updateRetryCount(issue.id, 0);
      this.issueRepo!.updateBlockedReason(issue.id, null);
      log.info('Recovered build-stage orphan — all tasks pass, auto-advanced to review', {
        issueNumber: issue.number,
        totalTasks: tasksFile.tasks.length,
        action: 'stage=check, approval_state=awaiting, status remains active',
      });
    } else {
      const passed = tasksFile.tasks.filter(t => t.passes === true).length;
      const pending = tasksFile.tasks.filter(t => t.passes !== true);
      const pendingIds = pending.map(t => t.id).join(', ');
      const currentRetryCount = issue.retryCount ?? 0;
      const newRetryCount = currentRetryCount + 1;

      if (newRetryCount > MAX_RETRIES) {
        const reason = `${passed}/${tasksFile.tasks.length} 任务完成，${pendingIds} 待完成 — 已自动重试 ${MAX_RETRIES} 次仍失败，需人工介入`;
        this.issueRepo!.blockIssue(issue.id, reason);
        this.issueRepo!.updateRetryCount(issue.id, newRetryCount);
        this.issueRepo!.clearApprovalState(issue.id);
        this.emitBlocked(issue, reason, newRetryCount);
        log.info('Recovered build-stage orphan — max retries reached', {
          issueNumber: issue.number,
          action: `status=blocked, ${passed}/${tasksFile.tasks.length} tasks, retries exhausted`,
        });
      } else {
        const reason = `${passed}/${tasksFile.tasks.length} 任务完成，${pendingIds} 待完成 — 第 ${newRetryCount}/${MAX_RETRIES} 次自动重试`;
        this.issueRepo!.updateRetryCount(issue.id, newRetryCount);
        this.issueRepo!.updateBlockedReason(issue.id, reason);
        this.issueRepo!.clearApprovalState(issue.id);

        try {
          this.enqueue(issue.id, 'resume-pipeline');
          log.info('Recovered build-stage orphan — auto-retry enqueued', {
            issueNumber: issue.number,
            action: `retry ${newRetryCount}/${MAX_RETRIES}, ${passed}/${tasksFile.tasks.length} tasks`,
          });
        } catch (enqueueErr) {
          const failReason = `自动重试启动失败: ${enqueueErr instanceof Error ? enqueueErr.message : String(enqueueErr)} (${passed}/${tasksFile.tasks.length} 任务完成)`;
          this.issueRepo!.blockIssue(issue.id, failReason);
          this.emitBlocked(issue, failReason, newRetryCount);
          log.info('Recovered build-stage orphan — auto-retry enqueue failed', {
            issueNumber: issue.number,
            action: 'status=blocked, enqueue failed',
          });
        }
      }
    }
  }

  private emitBlocked(issue: Issue, reason: string, retryCount: number): void {
    try {
      this.eventBus.emit('agent_blocked', {
        issueId: issue.id,
        projectId: issue.projectId,
        issueNumber: issue.number,
        blockedReason: reason,
        retryCount,
      });
    } catch (emitErr) {
      log.error('Failed to emit agent_blocked event', {
        issueNumber: issue.number,
        error: emitErr instanceof Error ? emitErr.message : String(emitErr),
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
          case 'rebase':
            await this.executeRebaseTask(task, issue);
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

    if (issue.status === IssueStatus.Blocked) {
      log.info('Skipping start-pipeline: issue is blocked', { issueNumber: issue.number });
      this.completeTask(task.id, 'completed', 'skipped');
      return;
    }

    if (issue.stage !== Stage.Draft && issue.stage !== Stage.Backlog) {
      log.info('Skipping start-pipeline: issue not in draft/backlog', { issueNumber: issue.number, stage: issue.stage });
      this.completeTask(task.id, 'completed', 'skipped');
      return;
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

    const acpOptions: AcpConnectionOptions = { cwd: worktreePath };
    await this.runPipelineToCompletion(task, issue, issue.projectId, this.issueRepo, worktreePath, acpOptions);
  }

  private async executeResumePipelineTask(task: IssueTaskQueueRecord, issue: Issue): Promise<void> {
    if (!this.issueRepo || !this.projectRepo || !this.worktreeManager) {
      this.completeTask(task.id, 'failed', 'Missing dependencies for pipeline execution');
      return;
    }

    if (issue.status === IssueStatus.Blocked) {
      if (issue.approvalState?.status === 'approved') {
        log.info('Unblocking approved issue before resume', { issueNumber: issue.number });
        this.issueRepo.updateStatus(issue.id, IssueStatus.Active);
        this.issueRepo.updateBlockedReason(issue.id, null);
      } else {
        log.info('Skipping resume-pipeline: issue is blocked', { issueNumber: issue.number });
        this.completeTask(task.id, 'completed', 'skipped');
        return;
      }
    }

    if (issue.stage === Stage.Done) {
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

    const acpOptions: AcpConnectionOptions = { cwd: worktreePath };
    await this.runPipelineToCompletion(task, issue, issue.projectId, this.issueRepo, worktreePath, acpOptions);
  }

  private async runPipelineToCompletion(
    task: IssueTaskQueueRecord,
    issue: Issue,
    projectId: string,
    issueRepo: IssueRepo,
    worktreePath: string,
    acpOptions: AcpConnectionOptions,
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
      const runners = [
        new PlanStageRunner(),
        new BuildStageRunner({ worktreePath, projectId }),
        new CheckStageRunner([
          new BuildTestCheck({ worktreePath }),
          new AiReviewCheck(),
        ]),
      ];
      const pipeline = new WorkflowEngine({
        runners,
        artifactManager,
        issueRepo,
        eventBus: this.eventBus,
        projectId,
        checkpointManager,
        signal: abortController.signal,
        coderSessionRepo: this.coderSessionRepo,
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
      log.info('Pipeline run completed', { issueNumber: issue.number, elapsedMs: duration, completed: result.completed, gateRequired: result.gateRequired });

      if (result.gateRequired) {
        this.eventBus.emit('agent_paused', {
          issueId: issue.id,
          projectId,
          issueNumber: issue.number,
        });
        log.info('Pipeline paused at gate, marking task completed', {
          issueNumber: issue.number,
          stage: result.stage,
          taskId: task.id,
        });
        this.completeTask(task.id, 'completed', 'approval_gate');
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
      const currentIssue = issueRepo.findById(issue.id);
      issueRepo.setApprovalState(issue.id, {
        stage: currentIssue?.stage ?? Stage.Draft,
        status: 'error',
        output: { error: errorMsg },
        requestedAt: new Date().toISOString(),
      });
    } catch (stateErr) {
      log.error('Failed to set error approval state', {
        issueNumber: issue.number,
        error: stateErr instanceof Error ? stateErr.message : String(stateErr),
      });
    }
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

  private async executeRebaseTask(task: IssueTaskQueueRecord, issue: Issue): Promise<void> {
    if (!this.issueRepo || !this.projectRepo || !this.worktreeManager) {
      this.completeTask(task.id, 'failed', 'Missing dependencies for rebase execution');
      return;
    }

    if (!REBASE_ALLOWED_STAGES.includes(issue.stage)) {
      log.info('Skipping rebase: stage not allowed', { issueNumber: issue.number, stage: issue.stage });
      this.completeTask(task.id, 'completed', 'skipped');
      return;
    }

    const project = this.projectRepo.findById(issue.projectId);
    if (!project) {
      this.completeTask(task.id, 'failed', `Project not found: ${issue.projectId}`);
      return;
    }

    if (!this.worktreeManager.exists(project.name, issue.number)) {
      this.completeTask(task.id, 'failed', `Worktree not found for issue #${issue.number}`);
      return;
    }

    const payload = this.parsePayload(task.payload);
    const reEvalPlan = Boolean(payload.reEvalPlan);
    const issueNumber = issue.number;
    const issueId = issue.id;
    const projectId = issue.projectId;

    this.eventBus.emit('rebase_started', { issueId, projectId, issueNumber });

    this.eventBus.emit('rebase_progress', { issueId, projectId, issueNumber, step: 'fetching' });
    await smartFetch(project.path);

    this.eventBus.emit('rebase_progress', { issueId, projectId, issueNumber, step: 'checking' });
    const canFF = await this.worktreeManager.canFastForward(project.path, project.name, issueNumber, project.baseBranch);

    if (canFF) {
      this.eventBus.emit('rebase_completed', { issueId, projectId, issueNumber, rebased: false });
      this.completeTask(task.id, 'completed', 'up_to_date');
      return;
    }

    this.eventBus.emit('rebase_progress', { issueId, projectId, issueNumber, step: 'rebasing' });
    const rebaseResult = await this.worktreeManager.rebaseOntoMaster(
      project.path,
      project.name,
      issueNumber,
      project.baseBranch,
      { abortOnConflict: false },
    );

    if (!rebaseResult.success) {
      if (!this.conflictResolutionDeps) {
        await this.worktreeManager.abortRebase(project.name, issueNumber);
        this.eventBus.emit('rebase_conflict', {
          issueId,
          projectId,
          issueNumber,
          conflicts: rebaseResult.conflicts,
        });
        this.completeTask(task.id, 'failed', 'Rebase conflicts, no auto-resolution available');
        return;
      }

      const worktreePath = this.worktreeManager.getPath(project.name, issueNumber);
      if (!worktreePath) {
        await this.worktreeManager.abortRebase(project.name, issueNumber);
        this.completeTask(task.id, 'failed', 'Worktree path not found');
        return;
      }

      this.eventBus.emit('rebase_conflict', {
        issueId,
        projectId,
        issueNumber,
        conflicts: rebaseResult.conflicts,
        status: 'resolving',
      });
      this.eventBus.emit('agent_conflict_resolution_started', {
        issueId,
        projectId,
        issueNumber,
        conflictFiles: rebaseResult.conflicts,
      });

      try {
        const resolutionResult = await resolveConflictsViaAgent(
          this.conflictResolutionDeps,
          issueId,
          projectId,
          worktreePath,
          rebaseResult.conflicts,
        );

        if (!resolutionResult.success) {
          await this.worktreeManager.abortRebase(project.name, issueNumber);
          this.eventBus.emit('agent_conflict_resolution_failed', {
            issueId,
            projectId,
            issueNumber,
            error: resolutionResult.error || 'Conflict resolution failed',
          });
          this.eventBus.emit('rebase_conflict', {
            issueId,
            projectId,
            issueNumber,
            conflicts: rebaseResult.conflicts,
            status: 'failed',
            error: resolutionResult.error || 'Conflict resolution failed',
          });
          this.completeTask(task.id, 'failed', resolutionResult.error || 'Conflict resolution failed');
          return;
        }

        this.eventBus.emit('agent_conflict_resolution_completed', {
          issueId,
          projectId,
          issueNumber,
        });

        const refreshedIssue = this.issueRepo!.findById(issueId);

        if (refreshedIssue?.stage === Stage.Check) {
          await this.handleReviewRebase(refreshedIssue, project, projectId, issueNumber, true);
        }

        this.eventBus.emit('rebase_progress', { issueId, projectId, issueNumber, step: 'completing' });
        this.eventBus.emit('rebase_completed', { issueId, projectId, issueNumber, rebased: true });

        if (refreshedIssue?.stage === Stage.Plan) {
          this.handlePlanRebase(refreshedIssue, project, projectId, issueNumber);
        }

        if (refreshedIssue?.stage === Stage.Build) {
          this.handleBuildRebase(refreshedIssue, project, projectId, issueNumber, reEvalPlan);
        }

        this.completeTask(task.id, 'completed', 'success');
      } catch (err) {
        log.error('Unexpected error in conflict resolution', { issueNumber, error: err instanceof Error ? err.message : String(err) });
        try {
          await this.worktreeManager.abortRebase(project.name, issueNumber);
        } catch {}
        this.eventBus.emit('rebase_conflict', {
          issueId,
          projectId,
          issueNumber,
          conflicts: rebaseResult.conflicts,
          status: 'failed',
          error: err instanceof Error ? err.message : 'Unexpected error during conflict resolution',
        });
        this.completeTask(task.id, 'failed', err instanceof Error ? err.message : 'Unexpected error during conflict resolution');
      }
      return;
    }

    if (issue.stage === Stage.Check) {
      await this.handleReviewRebase(issue, project, projectId, issueNumber);
    }

    this.eventBus.emit('rebase_completed', { issueId, projectId, issueNumber, rebased: true });

    if (issue.stage === Stage.Plan) {
      this.handlePlanRebase(issue, project, projectId, issueNumber);
    }

    if (issue.stage === Stage.Build) {
      this.handleBuildRebase(issue, project, projectId, issueNumber, reEvalPlan);
    }

    this.completeTask(task.id, 'completed', 'success');
  }

  private async handleReviewRebase(
    issue: Issue,
    project: { name: string; baseBranch: string },
    projectId: string,
    number: number,
    skipBuildVerify?: boolean,
  ): Promise<boolean | undefined> {
    if (!this.worktreeManager) return undefined;
    if (skipBuildVerify) {
      log.info('Skipping build verification after conflict resolution', { issueNumber: number });
      return undefined;
    }
    this.eventBus.emit('rebase_progress', { issueId: issue.id, projectId, issueNumber: number, step: 'verifying' });
    try {
      const worktreePath = this.worktreeManager.getPath(project.name, issue.number);
      if (worktreePath) {
        await execFileAsync('npm', ['run', 'build'], {
          cwd: worktreePath,
          timeout: 5 * 60 * 1000,
          maxBuffer: 10 * 1024 * 1024,
        });
        return true;
      }
    } catch {}
    return false;
  }

  private handlePlanRebase(issue: Issue, _project: { name: string }, _projectId: string, number: number): void {
    if (!this.worktreeManager || !this.issueRepo) return;
    if (!this.isIssueAtApprovalGate(issue.id)) {
      log.info('Skipping re-self-review injection: issue not at approval gate', { issueNumber: number });
      return;
    }
    const rebaseMessage = 'master has new changes after rebase. Please re-evaluate design artifacts: check if design/tasks can leverage the new code, and verify all file paths referenced in tasks.json still exist in the updated codebase.';
    const commentRepo = (this as any).commentRepo;
    if (commentRepo) {
      commentRepo.create({ issueId: issue.id, body: rebaseMessage });
    }
    try {
      this.enqueue(issue.id, 'resume-pipeline');
      log.info('Enqueued resume-pipeline after plan rebase', { issueNumber: number });
    } catch (err) {
      log.error('Failed to enqueue resume-pipeline after plan rebase', {
        issueNumber: number,
        error: err instanceof Error ? err.message : String(err),
      });
    }
  }

  private handleBuildRebase(issue: Issue, project: { name: string }, _projectId: string, number: number, reEvalPlan = false): void {
    if (!this.worktreeManager || !this.issueRepo) return;

    if (reEvalPlan) {
      this.issueRepo.updateStage(issue.id, Stage.Plan);
      this.issueRepo.clearApprovalState(issue.id);
      const rebaseMessage = 'master has new changes after rebase. Code commits are preserved. Please re-evaluate design artifacts and tasks: check if the existing task breakdown is still appropriate for the updated codebase, merge/split/add/remove tasks as needed, and verify all file paths referenced in tasks.json still exist.';
      const commentRepo = (this as any).commentRepo;
      if (commentRepo) {
        commentRepo.create({ issueId: issue.id, body: rebaseMessage });
      }
      try {
        this.enqueue(issue.id, 'start-pipeline');
        log.info('Enqueued start-pipeline after build rebase (reEvalPlan)', { issueNumber: number });
      } catch (err) {
        log.error('Failed to enqueue start-pipeline after build rebase', {
          issueNumber: number,
          error: err instanceof Error ? err.message : String(err),
        });
      }
      return;
    }

    if (!this.checkpointRepo) return;
    const changeDir = findChangeDir(
      this.worktreeManager.getPath(project.name, issue.number) || process.cwd(),
      issue.number,
    );
    if (!changeDir) return;
    try {
      const tasksPath = path.join(changeDir, 'tasks.json');
      const tasksContent = fs.readFileSync(tasksPath, 'utf-8');
      const tasksFile = JSON.parse(tasksContent);
      for (const task of tasksFile.tasks) {
        task.passes = false;
        task.error = null;
        task.attempts = 0;
      }
      fs.writeFileSync(tasksPath, JSON.stringify(tasksFile, null, 2), 'utf-8');
      this.checkpointRepo.delete(issue.number, 'build');
    } catch (err) {
      log.warn('Failed to clear build checkpoint after rebase', { issueNumber: number, error: err instanceof Error ? err.message : String(err) });
    }
  }

  private parsePayload(payloadStr: string): Record<string, unknown> {
    try {
      return JSON.parse(payloadStr);
    } catch {
      return {};
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

  isIssueAtApprovalGate(issueId: string): boolean {
    if (!this.issueRepo) return false;
    const issue = this.issueRepo.findById(issueId);
    if (!issue) return false;
    return issue.approvalState?.status === 'awaiting';
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

