import type { IssueRepo } from '../db/issue-repo';
import type { CommentRepo } from '../db/comment-repo';
import { SessionManager, type LlmConfig } from '../agent-runtime';
import { runMainAgent } from '../agents/main-agent';
import { IssueStatus, type Issue } from '../types';
import { EventBus } from './event-bus';

export interface AgentStatus {
  running: boolean;
  issueId: string | null;
  issueNumber: number | null;
}

export class AgentRunnerService {
  private activeIssueId: string | null = null;
  private activeIssueNumber: number | null = null;
  private activePromise: Promise<void> | null = null;

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
      try {
        await runMainAgent(
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
        this.eventBus.emit('agent_completed', { issueId: issue.id, projectId });
      } catch (err) {
        const errorMsg = err instanceof Error ? err.message : String(err);
        console.error(`Agent loop failed for issue #${issue.number}:`, err);
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
