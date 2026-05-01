import type { IssueRepo } from '../db/issue-repo';
import type { ProjectRepo } from '../db/project-repo';
import type { LlmConfig } from '../agent-runtime';
import type { AcpConnectionOptions } from '../agent-runtime/acp-session';
import { WorkflowEngine, type PipelineResult, PlanStageRunner, BuildStageRunner, CheckStageRunner, BuildTestCheck, MergeReadyCheck, AiReviewCheck } from '../workflow';
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
import { WorktreeManager } from '../git/worktree-manager';
import * as fs from 'fs';
import * as path from 'path';

export interface RunningAgent {
  issueId: string;
  issueNumber: number;
  promise: Promise<void>;
  projectId: string;
  abortController: AbortController;
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
}

export interface AgentStatus {
  running: boolean;
  issueId: string | null;
  issueNumber: number | null;
  activeAgents: Array<{ issueId: string; issueNumber: number; projectId: string }>;
  waitingQuestions: Array<{ issueId: string; issueNumber: number; projectId: string; questionId: string; question: string }>;
  recoverableIssues: RecoverableIssue[];
  blockedIssues: BlockedIssueInfo[];
  queueDepth: number;
  maxConcurrentAgents: number;
}

export interface WaitingQuestion {
  questionId: string;
  question: string;
}

const log = Log.create({ service: 'agent-runner' });

export interface PipelineGateInfo {
  issueId: string;
  issueNumber: number;
  projectId: string;
  stage: Stage;
}

export class AgentRunnerService {
  private activeAgents = new Map<string, RunningAgent>();
  private pendingGates = new Map<number, PipelineGateInfo>();
  private waitingQuestions = new Map<string, WaitingQuestion>();
  private readonly maxConcurrentAgents: number;
  private recoverableIssues: RecoverableIssue[];
  private llmConfig?: LlmConfig;
  private readonly providersChangedListener: (data: { providers: Array<{ id: string; name?: string; apiKey?: string; baseURL?: string; sdk?: string; models?: string[] }> }) => void;

  constructor(
    private readonly eventBus: EventBus,
    _workflowLogRepo?: unknown,
    private readonly issueRepo?: IssueRepo,
    maxConcurrentAgents: number = 8,
    _agentSessionMessageRepo?: unknown,
    _coderSessionRepo?: unknown,
    private readonly checkpointRepo?: PipelineCheckpointRepo,
    private readonly projectRepo?: ProjectRepo,
    private readonly worktreeManager?: WorktreeManager,
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
  }

  shutdown(): void {
    this.eventBus.off('config:providers:changed', this.providersChangedListener);
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
    const agent = this.activeAgents.get(issueId);
    if (!agent) return { stopped: false };

    const { issueNumber, abortController } = agent;
    log.info('Force stopping agent', { issueId, issueNumber });
    abortController.abort();

    this.activeAgents.delete(issueId);
    this.pendingGates.delete(issueNumber);
    this.waitingQuestions.delete(issueId);

    this.eventBus.emit('agent_stopped', {
      issueId,
      projectId: agent.projectId,
      issueNumber,
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

  recoverIssues(): void {
    if (!this.issueRepo) return;

    const orphans = this.issueRepo.findAll({ status: IssueStatus.Active })
      .filter(issue => issue.stage !== Stage.Draft && issue.stage !== Stage.Backlog);

    if (orphans.length === 0) return;

    for (const issue of orphans) {
      try {
        if (issue.approvalState?.status === 'awaiting') {
          this.pendingGates.set(issue.number, {
            issueId: issue.id,
            issueNumber: issue.number,
            projectId: issue.projectId,
            stage: issue.approvalState.stage ?? issue.stage,
          });
          log.info('Restored pending gate for awaiting issue', {
            issueNumber: issue.number,
            stage: issue.approvalState.stage ?? issue.stage,
            action: 'pendingGate restored, status remains active',
          });
        } else if (issue.stage === Stage.Build && this.projectRepo && this.worktreeManager) {
          this.recoverBuildStageIssue(issue);
        } else if (this.isStageCompletedInDb(issue)) {
          this.issueRepo.setApprovalState(issue.id, {
            stage: issue.stage,
            status: 'awaiting',
            output: { recovered: true, reason: 'agent completed but approval_state not written' },
            requestedAt: new Date().toISOString(),
          });
          this.pendingGates.set(issue.number, {
            issueId: issue.id,
            issueNumber: issue.number,
            projectId: issue.projectId,
            stage: issue.stage,
          });
          log.info('Recovered orphaned issue — stage completed, restored approval gate', {
            issueNumber: issue.number,
            stage: issue.stage,
            action: 'approval_state=awaiting, pendingGate restored',
          });
        } else {
          this.issueRepo.updateStatus(issue.id, IssueStatus.Interrupted);
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

    this.recoverableIssues = [];
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

        const pipelineResult = this.startPipeline(
          issue,
          issue.projectId,
          this.issueRepo!,
          worktreePath,
          { cwd: worktreePath },
        );

        if (!pipelineResult.started) {
          const failReason = `自动重试启动失败: ${pipelineResult.error} (${passed}/${tasksFile.tasks.length} 任务完成)`;
          this.issueRepo!.blockIssue(issue.id, failReason);
          this.emitBlocked(issue, failReason, newRetryCount);
          log.info('Recovered build-stage orphan — auto-retry start failed', {
            issueNumber: issue.number,
            action: 'status=blocked, pipeline start failed',
          });
        } else {
          log.info('Recovered build-stage orphan — auto-retry started', {
            issueNumber: issue.number,
            action: `retry ${newRetryCount}/${MAX_RETRIES}, ${passed}/${tasksFile.tasks.length} tasks`,
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

  private isStageCompletedInDb(issue: Issue): boolean {
    if (!this.issueRepo) return false;

    const approvalStages: Stage[] = [Stage.Plan, Stage.Check];
    if (!approvalStages.includes(issue.stage)) return false;

    return this.issueRepo.hasCompletedCoderSession(issue.id, issue.stage);
  }

  getMaxConcurrentAgents(): number {
    return this.maxConcurrentAgents;
  }

  setWaiting(issueId: string, questionId: string, question: string): void {
    this.waitingQuestions.set(issueId, { questionId, question });
  }

  clearWaiting(issueId: string): void {
    this.waitingQuestions.delete(issueId);
  }

  getWaitingQuestions(): Map<string, WaitingQuestion> {
    return this.waitingQuestions;
  }

  isRunning(issueId?: string): boolean {
    if (issueId !== undefined) {
      return this.activeAgents.has(issueId);
    }
    return this.activeAgents.size > 0;
  }

  async stop(issueId: string): Promise<boolean> {
    const agent = this.activeAgents.get(issueId);
    if (!agent) return false;

    const { issueNumber, projectId, abortController, promise } = agent;

    log.info('Stopping agent', { issueId, issueNumber });

    abortController.abort();

    try {
      await promise;
    } catch {
      // expected — the abort rejection
    }

    this.activeAgents.delete(issueId);
    this.pendingGates.delete(issueNumber);
    this.waitingQuestions.delete(issueId);

    if (this.issueRepo) {
      try {
        this.issueRepo.blockIssue(issueId, '用户手动停止 agent');
        this.issueRepo.clearApprovalState(issueId);
      } catch (err) {
        log.error('Failed to update issue status after stop', {
          issueNumber,
          error: err instanceof Error ? err.message : String(err),
        });
      }
    }

    this.eventBus.emit('agent_stopped', { issueId, projectId, issueNumber, reason: 'user_stop' });
    log.info('Agent stopped', { issueId, issueNumber });

    return true;
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
    }));
  }

  getStatus(): AgentStatus {
    const agents = Array.from(this.activeAgents.values()).map((a) => ({
      issueId: a.issueId,
      issueNumber: a.issueNumber,
      projectId: a.projectId,
    }));

    const waiting = Array.from(this.waitingQuestions.entries()).map(([issueId, wq]) => {
      const agent = this.activeAgents.get(issueId);
      return {
        issueId,
        issueNumber: agent?.issueNumber ?? 0,
        projectId: agent?.projectId ?? '',
        questionId: wq.questionId,
        question: wq.question,
      };
    });

    const first = agents[0];
    const blockedIssues = this.getBlockedIssues();

    return {
      running: this.activeAgents.size > 0,
      issueId: first != null ? first.issueId : null,
      issueNumber: first != null ? first.issueNumber : null,
      activeAgents: agents,
      waitingQuestions: waiting,
      recoverableIssues: this.recoverableIssues,
      blockedIssues: blockedIssues,
      queueDepth: 0,
      maxConcurrentAgents: this.maxConcurrentAgents,
    };
  }

  getActiveIssueId(): string | null {
    if (this.activeAgents.size === 0) return null;
    const first = this.activeAgents.values().next().value;
    return first != null ? first.issueId : null;
  }

  hasPendingGate(issueNumber: number): boolean {
    return this.pendingGates.has(issueNumber);
  }

  startPipeline(
    issue: Issue,
    projectId: string,
    issueRepo: IssueRepo,
    worktreePath: string,
    acpOptions: AcpConnectionOptions,
    _updateIssueStatus?: (issueId: string, status: IssueStatus) => void,
  ): { started: boolean; error?: string } {
    if (this.activeAgents.has(issue.id)) {
      return { started: false, error: `Issue #${issue.number} already has an agent running` };
    }

    if (this.activeAgents.size >= this.maxConcurrentAgents) {
      return { started: false, error: `Concurrent agent limit reached (${this.maxConcurrentAgents})` };
    }

    const pendingApproval = issueRepo.findPendingApprovalByIssueId(issue.id);
    if (pendingApproval) {
      return {
        started: false,
        error: `Issue #${issue.number} has pending approval.`,
      };
    }

    this.executePipeline(issue, projectId, issueRepo, worktreePath, acpOptions);
    return { started: true };
  }

  resumePipeline(
    issue: Issue,
    projectId: string,
    issueRepo: IssueRepo,
    worktreePath: string,
    acpOptions: AcpConnectionOptions,
    _updateIssueStatus?: (issueId: string, status: IssueStatus) => void,
  ): void {
    if (this.activeAgents.has(issue.id)) {
      throw new Error(`Issue #${issue.number} is already running`);
    }
    this.pendingGates.delete(issue.number);
    this.executePipeline(issue, projectId, issueRepo, worktreePath, acpOptions);
  }

  private executePipeline(
    issue: Issue,
    projectId: string,
    issueRepo: IssueRepo,
    worktreePath: string,
    acpOptions: AcpConnectionOptions,
  ): void {
    this.eventBus.emit('agent_started', { issueId: issue.id, projectId });
    log.info('Pipeline started', { issueNumber: issue.number, projectId });

    const abortController = new AbortController();
    const startTime = Date.now();
    const promise = (async () => {
      try {
        const artifactManager = new ChangeArtifactsManager(worktreePath);
        const checkpointManager = this.checkpointRepo
          ? createCheckpointManager(this.checkpointRepo)
          : createCheckpointManager({ get: () => null, upsert: () => {}, delete: () => {} } as any);
        const runners = [
          new PlanStageRunner(),
          new BuildStageRunner({ worktreePath, projectId }),
          new CheckStageRunner([
            new BuildTestCheck({ worktreePath }),
            new MergeReadyCheck({ worktreeManager: this.worktreeManager, projectRepo: this.projectRepo as any }),
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
        });

        const abortPromise = new Promise<never>((_resolve, reject) => {
          abortController.signal.addEventListener('abort', () => {
            reject(new Error('Agent stopped by user'));
          });
        });

        const result: PipelineResult = await Promise.race([
          pipeline.run(issue, acpOptions),
          abortPromise,
        ]);

        if (result.gateRequired) {
          this.pendingGates.set(issue.number, {
            issueId: issue.id,
            issueNumber: issue.number,
            projectId,
            stage: result.stage,
          });
          this.eventBus.emit('agent_paused', {
            issueId: issue.id,
            projectId,
            issueNumber: issue.number,
          });
          log.info('Pipeline paused at gate', {
            issueNumber: issue.number,
            stage: result.stage,
          });
        }

        const duration = Date.now() - startTime;
        log.info('Pipeline run completed', { issueNumber: issue.number, elapsedMs: duration, completed: result.completed });
        if (result.completed) {
          this.eventBus.emit('agent_completed', { issueId: issue.id, projectId, issueNumber: issue.number });
        } else if (!result.gateRequired) {
          try {
            issueRepo.setApprovalState(issue.id, {
              stage: result.stage,
              status: 'error',
              output: { error: result.message ?? 'Pipeline failed without completing' },
              requestedAt: new Date().toISOString(),
            });
          } catch (stateErr) {
            log.error('Failed to set error approval state', {
              issueNumber: issue.number,
              error: stateErr instanceof Error ? stateErr.message : String(stateErr),
            });
          }
          try {
            issueRepo.blockIssue(issue.id, result.message ?? 'Pipeline failed without completing');
          } catch (updateErr) {
            log.error('Failed to block issue', {
              issueNumber: issue.number,
              error: updateErr instanceof Error ? updateErr.message : String(updateErr),
            });
          }
          try {
            this.eventBus.emit('agent_error', {
              issueId: issue.id,
              projectId,
              error: result.message ?? 'Pipeline failed without completing',
            });
          } catch (emitErr) {
            log.error('Failed to emit agent_error event', {
              issueNumber: issue.number,
              error: emitErr instanceof Error ? emitErr.message : String(emitErr),
            });
          }
        }
      } catch (err) {
        const errorMsg = err instanceof Error ? err.message : String(err);
        const currentIssue = issueRepo.findById(issue.id);
        log.error('Pipeline execution failed', {
          issueNumber: issue.number,
          stage: currentIssue?.stage ?? 'unknown',
          error: errorMsg,
        });
        try {
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
          this.eventBus.emit('agent_error', {
            issueId: issue.id,
            projectId,
            error: errorMsg,
          });
        } catch (emitErr) {
          log.error('Failed to emit agent_error event', {
            issueNumber: issue.number,
            error: emitErr instanceof Error ? emitErr.message : String(emitErr),
          });
        }
      } finally {
        this.activeAgents.delete(issue.id);
        this.clearWaiting(issue.id);
      }
    })();

    this.activeAgents.set(issue.id, {
      issueId: issue.id,
      issueNumber: issue.number,
      promise,
      projectId,
      abortController,
    });
  }
}
