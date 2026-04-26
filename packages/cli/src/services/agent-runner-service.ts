import type { IssueRepo } from '../db/issue-repo';
import type { LlmConfig } from '../agent-runtime';
import type { AcpConnectionOptions } from '../agent-runtime/acp-session';
import { WorkflowController, type PipelineResult } from '../workflow/workflow-controller';
import { ChangeArtifactsManager } from '../artifacts/change-artifacts-manager';
import { IssueStatus, type Issue } from '../types';
import { EventBus } from './event-bus';
import { Stage } from '../types';
import { load } from '../config/config-loader';
import { maskSensitiveData } from '../utils/sensitive-data';
import { Log } from '../util/log';

export interface RunningAgent {
  issueId: string;
  issueNumber: number;
  promise: Promise<void>;
  projectId: string;
}

export interface RecoverableIssue {
  issueNumber: number;
  stage: string;
}

export interface AgentStatus {
  running: boolean;
  issueId: string | null;
  issueNumber: number | null;
  activeAgents: Array<{ issueId: string; issueNumber: number; projectId: string }>;
  waitingQuestions: Array<{ issueId: string; issueNumber: number; projectId: string; questionId: string; question: string }>;
  recoverableIssues: RecoverableIssue[];
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

  private detectRecoverableIssues(): RecoverableIssue[] {
    if (!this.issueRepo) return [];
    const activeIssues = this.issueRepo.findAll({ status: IssueStatus.Active });
    return activeIssues
      .filter(issue => issue.stage !== Stage.Draft)
      .map(issue => ({ issueNumber: issue.number, stage: issue.stage }));
  }

  recoverIssues(): void {
    if (!this.issueRepo) return;

    const orphans = this.issueRepo.findAll({ status: IssueStatus.Active })
      .filter(issue => issue.stage !== Stage.Draft);

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
        } else {
          this.issueRepo.updateStatus(issue.id, IssueStatus.Blocked);
          this.issueRepo.clearApprovalState(issue.id);
          log.info('Recovered orphaned issue', {
            issueNumber: issue.number,
            stage: issue.stage,
            action: 'status=blocked, stage preserved, approval cleared',
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

    return {
      running: this.activeAgents.size > 0,
      issueId: first != null ? first.issueId : null,
      issueNumber: first != null ? first.issueNumber : null,
      activeAgents: agents,
      waitingQuestions: waiting,
      recoverableIssues: this.recoverableIssues,
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
    updateIssueStatus?: (issueId: string, status: IssueStatus) => void,
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

    this.executePipeline(issue, projectId, issueRepo, worktreePath, acpOptions, updateIssueStatus);
    return { started: true };
  }

  resumePipeline(
    issue: Issue,
    projectId: string,
    issueRepo: IssueRepo,
    worktreePath: string,
    acpOptions: AcpConnectionOptions,
    updateIssueStatus?: (issueId: string, status: IssueStatus) => void,
  ): void {
    if (this.activeAgents.has(issue.id)) {
      throw new Error(`Issue #${issue.number} is already running`);
    }

    this.pendingGates.delete(issue.number);
    this.executePipeline(issue, projectId, issueRepo, worktreePath, acpOptions, updateIssueStatus);
  }

  private executePipeline(
    issue: Issue,
    projectId: string,
    issueRepo: IssueRepo,
    worktreePath: string,
    acpOptions: AcpConnectionOptions,
    updateIssueStatus?: (issueId: string, status: IssueStatus) => void,
  ): void {
    this.eventBus.emit('agent_started', { issueId: issue.id, projectId });
    log.info('Pipeline started', { issueNumber: issue.number, projectId });

    const startTime = Date.now();
    const promise = (async () => {
      try {
        const artifactManager = new ChangeArtifactsManager(worktreePath);
        const pipeline = new WorkflowController({
          artifactManager,
          worktreePath,
          issueRepo,
          eventBus: this.eventBus,
          projectId,
        });

        const result: PipelineResult = await pipeline.run(issue, acpOptions);

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
        log.info('Pipeline run completed', { issueNumber: issue.number, duration, completed: result.completed });
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
            updateIssueStatus?.(issue.id, IssueStatus.Blocked);
          } catch (updateErr) {
            log.error('Failed to update issue status to blocked', {
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
          updateIssueStatus?.(issue.id, IssueStatus.Blocked);
        } catch (updateErr) {
          log.error('Failed to update issue status to blocked', {
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
    });
  }
}
