import type { IssueRepo } from '../db/issue-repo';
import type { CommentRepo } from '../db/comment-repo';
import type { QuestionRepo } from '../db/question-repo';
import type { WorkflowLogRepo } from '../db/workflow-log-repo';
import { SessionManager, type Session, type LlmConfig } from '../agent-runtime';
import { runMainAgent } from '../agents/main-agent';
import { IssueStatus, type Issue } from '../types';
import { EventBus } from './event-bus';
import { Stage } from '../types';
import { loadWorkflow } from '../workflow/workflow-loader';
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
}

export interface QueuedAgent {
  issueId: string;
  issueNumber: number;
  projectId: string;
  issue: Issue;
  issueRepo: IssueRepo;
  commentRepo: CommentRepo;
  questionRepo?: QuestionRepo;
  worktreePath: string;
  sessionManager: SessionManager;
  llmConfig?: LlmConfig;
  updateIssueStatus?: (issueId: string, status: IssueStatus) => void;
}

export interface WaitingQuestion {
  questionId: string;
  question: string;
}

const log = Log.create({ service: 'agent-runner' });

export class AgentRunnerService {
  private activeAgents = new Map<string, RunningAgent>();
  private agentQueue: QueuedAgent[] = [];
  private pausedSessions = new Map<number, Session>();
  private waitingQuestions = new Map<string, WaitingQuestion>();
  private readonly maxConcurrentAgents: number;
  private readonly recoverableIssues: RecoverableIssue[];
  private llmConfig?: LlmConfig;
  private readonly providersChangedListener: (data: { providers: Array<{ id: string; name?: string; apiKey?: string; baseURL?: string; sdk?: string; models?: string[] }> }) => void;

  constructor(
    private readonly eventBus: EventBus,
    private readonly workflowLogRepo?: WorkflowLogRepo,
    private readonly issueRepo?: IssueRepo,
    maxConcurrentAgents: number = 8,
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
      queueDepth: this.agentQueue.length,
    };
  }

  getActiveIssueId(): string | null {
    if (this.activeAgents.size === 0) return null;
    const first = this.activeAgents.values().next().value;
    return first != null ? first.issueId : null;
  }

  hasPausedSession(issueNumber: number): boolean {
    return this.pausedSessions.has(issueNumber);
  }

  private shouldPauseAtCurrentStage(
    currentStage: string,
    worktreePath: string,
  ): boolean {
    if (currentStage === Stage.Done) return false;

    const workflow = loadWorkflow(worktreePath);
    if (typeof workflow === 'string') return false;

    const currentIndex = workflow.stages.findIndex((s) => s.stage === currentStage);
    if (currentIndex === -1 || currentIndex === workflow.stages.length - 1) return false;

    const nextStageConfig = workflow.stages[currentIndex + 1];
    return nextStageConfig?.approval === true;
  }

  getQueueSize(): number {
    return this.agentQueue.length;
  }

  private processQueue(): void {
    while (this.agentQueue.length > 0 && this.activeAgents.size < this.maxConcurrentAgents) {
      const queued = this.agentQueue.shift()!;
      this.executeAgent(
        queued.issue,
        queued.projectId,
        queued.issueRepo,
        queued.commentRepo,
        queued.questionRepo,
        queued.worktreePath,
        queued.sessionManager,
        queued.llmConfig,
        queued.updateIssueStatus,
      );
    }
  }

  private executeAgent(
    issue: Issue,
    projectId: string,
    issueRepo: IssueRepo,
    commentRepo: CommentRepo,
    questionRepo: QuestionRepo | undefined,
    worktreePath: string,
    sessionManager: SessionManager,
    llmConfig?: LlmConfig,
    updateIssueStatus?: (issueId: string, status: IssueStatus) => void,
  ): void {
    const resolvedLlmConfig = llmConfig ?? this.llmConfig;
    this.eventBus.emit('agent_started', { issueId: issue.id, projectId });
    log.info('Agent started', { issueNumber: issue.number, projectId });

    const startTime = Date.now();
    const promise = (async () => {
      let session: Session | undefined;
      try {
        const result = await runMainAgent(
          {
            issueRepo,
            commentRepo,
            questionRepo,
            worktreePath,
            llmConfig: resolvedLlmConfig,
            issue,
            eventBus: this.eventBus,
            workflowLogRepo: this.workflowLogRepo,
            onWaitingChange: (issueId, questionId, question) => {
              if (questionId && question) {
                this.setWaiting(issueId, questionId, question);
              } else {
                this.clearWaiting(issueId);
              }
            },
          },
          sessionManager,
        );
        session = result.session;

        const currentIssue = issueRepo.findById(issue.id);
        const shouldPause = currentIssue
          && this.shouldPauseAtCurrentStage(currentIssue.stage, worktreePath);

        if (shouldPause) {
          sessionManager.pause(session.id);
          this.pausedSessions.set(issue.number, session);
          this.eventBus.emit('agent_paused', { issueId: issue.id, projectId, issueNumber: issue.number });
        } else {
          sessionManager.close(session.id);
        }

        const duration = Date.now() - startTime;
        log.info('Agent completed', { issueNumber: issue.number, duration });
        this.eventBus.emit('agent_completed', { issueId: issue.id, projectId });
      } catch (err) {
        const errorMsg = err instanceof Error ? err.message : String(err);
        const currentIssue = issueRepo.findById(issue.id);
        log.error('Agent execution failed', {
          issueNumber: issue.number,
          stage: currentIssue?.stage ?? 'unknown',
          error: errorMsg,
        });
        if (session) {
          try { sessionManager.close(session.id); } catch (_) { /* already closed */ }
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
          issueRepo.updateStage(issue.id, Stage.Draft);
          issueRepo.clearApprovalState(issue.id);
        } catch (rollbackErr) {
          log.error('Failed to rollback stage to draft', {
            issueNumber: issue.number,
            error: rollbackErr instanceof Error ? rollbackErr.message : String(rollbackErr),
          });
        }
        this.eventBus.emit('agent_error', {
          issueId: issue.id,
          projectId,
          error: errorMsg,
        });
      } finally {
        this.activeAgents.delete(issue.id);
        this.clearWaiting(issue.id);
        this.processQueue();
      }
    })();

    this.activeAgents.set(issue.id, {
      issueId: issue.id,
      issueNumber: issue.number,
      promise,
      projectId,
    });
  }

  start(
    issue: Issue,
    projectId: string,
    issueRepo: IssueRepo,
    commentRepo: CommentRepo,
    questionRepo: QuestionRepo | undefined,
    worktreePath: string,
    sessionManager: SessionManager,
    llmConfig?: LlmConfig,
    updateIssueStatus?: (issueId: string, status: IssueStatus) => void,
  ): { started: boolean; queuePosition?: number; error?: string } {
    if (this.activeAgents.has(issue.id)) {
      return { started: false, queuePosition: 0 };
    }

    const pendingApproval = issueRepo.findPendingApprovalByIssueId(issue.id);
    if (pendingApproval) {
      return {
        started: false,
        error: `Issue #${issue.number} has pending approval. Use resume or submit_approval first.`,
      };
    }

    if (this.activeAgents.size >= this.maxConcurrentAgents) {
      this.agentQueue.push({
        issueId: issue.id,
        issueNumber: issue.number,
        projectId,
        issue,
        issueRepo,
        commentRepo,
        questionRepo,
        worktreePath,
        sessionManager,
        llmConfig,
        updateIssueStatus,
      });
      return { started: false, queuePosition: this.agentQueue.length };
    }

    this.executeAgent(issue, projectId, issueRepo, commentRepo, questionRepo, worktreePath, sessionManager, llmConfig, updateIssueStatus);
    return { started: true };
  }

  resume(
    issue: Issue,
    projectId: string,
    issueRepo: IssueRepo,
    commentRepo: CommentRepo,
    questionRepo: QuestionRepo | undefined,
    worktreePath: string,
    sessionManager: SessionManager,
    message: string,
    llmConfig?: LlmConfig,
    updateIssueStatus?: (issueId: string, status: IssueStatus) => void,
  ): void {
    if (this.activeAgents.has(issue.id)) {
      throw new Error(`Issue #${issue.number} is already running`);
    }

    const session = this.pausedSessions.get(issue.number);
    if (!session) {
      throw new Error(`No paused session found for issue #${issue.number}`);
    }

    this.pausedSessions.delete(issue.number);

    const resolvedLlmConfig = llmConfig ?? this.llmConfig;
    this.eventBus.emit('agent_started', { issueId: issue.id, projectId });
    log.info('Agent resumed', { issueNumber: issue.number, projectId });

    const startTime = Date.now();
    sessionManager.appendMessage(session.id, {
      role: 'user',
      content: message,
    });
    sessionManager.resume(session.id);

    const promise = (async () => {
      try {
        const { session: updatedSession } = await runMainAgent(
          {
            issueRepo,
            commentRepo,
            questionRepo,
            worktreePath,
            llmConfig: resolvedLlmConfig,
            issue,
            eventBus: this.eventBus,
            workflowLogRepo: this.workflowLogRepo,
            onWaitingChange: (issueId, questionId, question) => {
              if (questionId && question) {
                this.setWaiting(issueId, questionId, question);
              } else {
                this.clearWaiting(issueId);
              }
            },
          },
          sessionManager,
          session,
        );

        const currentIssue = issueRepo.findById(issue.id);
        const shouldPause = currentIssue
          && this.shouldPauseAtCurrentStage(currentIssue.stage, worktreePath);

        if (shouldPause) {
          sessionManager.pause(updatedSession.id);
          this.pausedSessions.set(issue.number, updatedSession);
          this.eventBus.emit('agent_paused', { issueId: issue.id, projectId, issueNumber: issue.number });
        } else {
          sessionManager.close(updatedSession.id);
        }

        const duration = Date.now() - startTime;
        log.info('Agent completed', { issueNumber: issue.number, duration });
        this.eventBus.emit('agent_completed', { issueId: issue.id, projectId });
      } catch (err) {
        const errorMsg = err instanceof Error ? err.message : String(err);
        const currentIssue = issueRepo.findById(issue.id);
        log.error('Agent execution failed', {
          issueNumber: issue.number,
          stage: currentIssue?.stage ?? 'unknown',
          error: errorMsg,
        });
        try { sessionManager.close(session.id); } catch (_) { /* already closed */ }
        try {
          updateIssueStatus?.(issue.id, IssueStatus.Blocked);
        } catch (updateErr) {
          log.error('Failed to update issue status to blocked', {
            issueNumber: issue.number,
            error: updateErr instanceof Error ? updateErr.message : String(updateErr),
          });
        }
        this.eventBus.emit('agent_error', {
          issueId: issue.id,
          projectId,
          error: errorMsg,
        });
      } finally {
        this.activeAgents.delete(issue.id);
        this.clearWaiting(issue.id);
        this.processQueue();
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
