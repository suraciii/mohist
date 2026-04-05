import type { IssueRepo } from '../db/issue-repo';
import type { CommentRepo } from '../db/comment-repo';
import { SessionManager, type Session, type LlmConfig } from '../agent-runtime';
import { runMainAgent } from '../agents/main-agent';
import { IssueStatus, type Issue } from '../types';
import { EventBus } from './event-bus';
import { Stage } from '../types';
import { loadWorkflow } from '../workflow/workflow-loader';

export interface AgentStatus {
  running: boolean;
  issueId: string | null;
  issueNumber: number | null;
}

export class AgentRunnerService {
  private activeIssueId: string | null = null;
  private activeIssueNumber: number | null = null;
  private activePromise: Promise<void> | null = null;
  private pausedSessions = new Map<number, Session>();

  constructor(
    private readonly eventBus: EventBus,
  ) {}

  isRunning(): boolean {
    return this.activePromise !== null;
  }

  getStatus(): AgentStatus {
    return {
      running: this.activePromise !== null,
      issueId: this.activeIssueId,
      issueNumber: this.activeIssueNumber,
    };
  }

  getActiveIssueId(): string | null {
    return this.activeIssueId;
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

  start(
    issue: Issue,
    projectId: string,
    issueRepo: IssueRepo,
    commentRepo: CommentRepo,
    worktreePath: string,
    sessionManager: SessionManager,
    llmConfig?: LlmConfig,
    updateIssueStatus?: (issueId: string, status: IssueStatus) => void,
  ): void {
    if (this.activePromise) {
      throw new Error(`Agent already running on issue #${this.activeIssueNumber}`);
    }

    this.activeIssueId = issue.id;
    this.activeIssueNumber = issue.number;

    this.eventBus.emit('agent_started', { issueId: issue.id, projectId });

    this.activePromise = (async () => {
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
        this.activePromise = null;
        this.activeIssueId = null;
        this.activeIssueNumber = null;
      }
    })();
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
    if (this.activePromise) {
      throw new Error(`Agent already running on issue #${this.activeIssueNumber}`);
    }

    const session = this.pausedSessions.get(issue.number);
    if (!session) {
      throw new Error(`No paused session found for issue #${issue.number}`);
    }

    this.pausedSessions.delete(issue.number);
    this.activeIssueId = issue.id;
    this.activeIssueNumber = issue.number;

    this.eventBus.emit('agent_started', { issueId: issue.id, projectId });

    sessionManager.appendMessage(session.id, {
      role: 'user',
      content: message,
    });
    sessionManager.resume(session.id);

    this.activePromise = (async () => {
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
        this.activePromise = null;
        this.activeIssueId = null;
        this.activeIssueNumber = null;
      }
    })();
  }
}
