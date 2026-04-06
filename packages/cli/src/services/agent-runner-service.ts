import type { IssueRepo } from '../db/issue-repo';
import type { CommentRepo } from '../db/comment-repo';
import { SessionManager, type Session, type LlmConfig } from '../agent-runtime';
import { runMainAgent } from '../agents/main-agent';
import { IssueStatus, type Issue } from '../types';
import { EventBus } from './event-bus';
import { Stage } from '../types';
import { loadWorkflow } from '../workflow/workflow-loader';

export interface RunningAgent {
  issueId: string;
  issueNumber: number;
  promise: Promise<void>;
  projectId: string;
}

export interface AgentStatus {
  running: boolean;
  issueId: string | null;
  issueNumber: number | null;
  activeAgents: Array<{ issueId: string; issueNumber: number; projectId: string }>;
  queueDepth: number;
}

export interface QueuedAgent {
  issueId: string;
  issueNumber: number;
  projectId: string;
  issue: Issue;
  issueRepo: IssueRepo;
  commentRepo: CommentRepo;
  worktreePath: string;
  sessionManager: SessionManager;
  llmConfig?: LlmConfig;
  updateIssueStatus?: (issueId: string, status: IssueStatus) => void;
}

export class AgentRunnerService {
  private activeAgents = new Map<string, RunningAgent>();
  private agentQueue: QueuedAgent[] = [];
  private pausedSessions = new Map<number, Session>();
  private readonly maxConcurrentAgents: number;

  constructor(
    private readonly eventBus: EventBus,
    maxConcurrentAgents: number = 8,
  ) {
    this.maxConcurrentAgents = maxConcurrentAgents;
    console.log(`AgentRunnerService initialized with maxConcurrentAgents: ${this.maxConcurrentAgents}`);
  }

  getMaxConcurrentAgents(): number {
    return this.maxConcurrentAgents;
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

    const first = agents[0];

    return {
      running: this.activeAgents.size > 0,
      issueId: first != null ? first.issueId : null,
      issueNumber: first != null ? first.issueNumber : null,
      activeAgents: agents,
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
    worktreePath: string,
    sessionManager: SessionManager,
    llmConfig?: LlmConfig,
    updateIssueStatus?: (issueId: string, status: IssueStatus) => void,
  ): void {
    this.eventBus.emit('agent_started', { issueId: issue.id, projectId });

    const promise = (async () => {
      let session: Session | undefined;
      try {
        const result = await runMainAgent(
          {
            issueRepo,
            commentRepo,
            worktreePath,
            llmConfig,
            issue,
            eventBus: this.eventBus,
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
          this.eventBus.emit('agent_paused', { issueId: issue.id, projectId });
        } else {
          sessionManager.close(session.id);
        }

        this.eventBus.emit('agent_completed', { issueId: issue.id, projectId });
      } catch (err) {
        const errorMsg = err instanceof Error ? err.message : String(err);
        console.error(`Agent loop failed for issue #${issue.number}:`, err);
        if (session) {
          try { sessionManager.close(session.id); } catch (_) { /* already closed */ }
        }
        try {
          updateIssueStatus?.(issue.id, IssueStatus.Blocked);
        } catch (updateErr) {
          console.error(`Failed to update issue #${issue.number} status to blocked:`, updateErr);
        }
        this.eventBus.emit('agent_error', {
          issueId: issue.id,
          projectId,
          error: errorMsg,
        });
      } finally {
        this.activeAgents.delete(issue.id);
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
    worktreePath: string,
    sessionManager: SessionManager,
    llmConfig?: LlmConfig,
    updateIssueStatus?: (issueId: string, status: IssueStatus) => void,
  ): { started: boolean; queuePosition?: number } {
    if (this.activeAgents.has(issue.id)) {
      return { started: false, queuePosition: 0 };
    }

    if (this.activeAgents.size >= this.maxConcurrentAgents) {
      this.agentQueue.push({
        issueId: issue.id,
        issueNumber: issue.number,
        projectId,
        issue,
        issueRepo,
        commentRepo,
        worktreePath,
        sessionManager,
        llmConfig,
        updateIssueStatus,
      });
      return { started: false, queuePosition: this.agentQueue.length };
    }

    this.executeAgent(issue, projectId, issueRepo, commentRepo, worktreePath, sessionManager, llmConfig, updateIssueStatus);
    return { started: true };
  }

  resume(
    issue: Issue,
    projectId: string,
    issueRepo: IssueRepo,
    commentRepo: CommentRepo,
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

    this.eventBus.emit('agent_started', { issueId: issue.id, projectId });

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
            worktreePath,
            llmConfig,
            issue,
            eventBus: this.eventBus,
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
          this.eventBus.emit('agent_paused', { issueId: issue.id, projectId });
        } else {
          sessionManager.close(updatedSession.id);
        }

        this.eventBus.emit('agent_completed', { issueId: issue.id, projectId });
      } catch (err) {
        const errorMsg = err instanceof Error ? err.message : String(err);
        console.error(`Agent loop failed for issue #${issue.number}:`, err);
        try { sessionManager.close(session.id); } catch (_) { /* already closed */ }
        try {
          updateIssueStatus?.(issue.id, IssueStatus.Blocked);
        } catch (updateErr) {
          console.error(`Failed to update issue #${issue.number} status to blocked:`, updateErr);
        }
        this.eventBus.emit('agent_error', {
          issueId: issue.id,
          projectId,
          error: errorMsg,
        });
      } finally {
        this.activeAgents.delete(issue.id);
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
