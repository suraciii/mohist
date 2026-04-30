import { Issue, Stage, IssueStatus, Comment, Priority } from '../types';
import { IssueRepo, CommentRepo, ProjectRepo, PipelineCheckpointRepo } from '../db';
import { WorktreeManager } from '../git/worktree-manager';
import { ChangeArtifactsManager } from '../artifacts/change-artifacts-manager';
import { Log } from '../util/log';

const log = Log.create({ service: 'issue' });

export interface CreateIssueInput {
  projectId: string;
  title: string;
  body?: string;
  labels?: string[];
  priority?: Priority;
}

export interface ArchiveOptions {
  cleanup?: boolean;
}

export interface ArchiveResult {
  issue: Issue;
  warning?: string;
}

export interface ArchiveAllResult {
  count: number;
  message: string;
}

export class IssueService {
  constructor(
    private issueRepo: IssueRepo,
    private commentRepo: CommentRepo,
    private projectRepo?: ProjectRepo,
    private worktreeManager?: WorktreeManager,
    private agentRunner?: { isRunning(issueId: string): boolean },
    private checkpointRepo?: PipelineCheckpointRepo,
  ) {}

  create(input: CreateIssueInput): Issue {
    const number = this.issueRepo.getNextNumber(input.projectId);
    
    return this.issueRepo.create({
      number,
      projectId: input.projectId,
      title: input.title,
      body: input.body,
      labels: input.labels,
      priority: input.priority || 'p2',
    });
  }

  getById(id: string): Issue | null {
    return this.issueRepo.findById(id);
  }

  getByNumber(projectId: string, number: number): Issue | null {
    return this.issueRepo.findByNumber(projectId, number);
  }

  getByProject(projectId: string): Issue[] {
    return this.issueRepo.findAll({ projectId });
  }

  getByStage(projectId: string, stage: Stage): Issue[] {
    return this.issueRepo.findByStage(projectId, stage);
  }

  getByStatus(projectId: string, status: IssueStatus): Issue[] {
    return this.issueRepo.findByStatus(projectId, status);
  }

  getByPriority(projectId: string, priority: Priority): Issue[] {
    return this.issueRepo.findAll({ projectId, priority });
  }

  getActive(projectId: string): Issue[] {
    return this.issueRepo.findActive(projectId);
  }

  transitionToStage(issueId: string, stage: Stage): Issue | null {
    const current = this.issueRepo.findById(issueId);
    const result = this.issueRepo.updateStage(issueId, stage);
    if (result && current) {
      log.info('Stage transition', { issueNumber: result.number, fromStage: current.stage, toStage: stage });
    }
    return result;
  }

  transitionToStageByNumber(projectId: string, number: number, stage: Stage): Issue | null {
    const issue = this.issueRepo.findByNumber(projectId, number);
    if (!issue) return null;
    
    const fromStage = issue.stage;
    const updated = this.issueRepo.updateStage(issue.id, stage);
    if (!updated) return null;

    log.info('Stage transition', { issueNumber: number, fromStage, toStage: stage });

    return this.issueRepo.findByNumber(projectId, number);
  }

  setStatus(issueId: string, status: IssueStatus): Issue | null {
    const current = this.issueRepo.findById(issueId);
    const result = this.issueRepo.updateStatus(issueId, status);
    if (result && current) {
      log.info('Status transition', { issueNumber: result.number, fromStatus: current.status, toStatus: status });
    }
    return result;
  }

  pause(projectId: string, number: number): Issue | null {
    const issue = this.issueRepo.findByNumber(projectId, number);
    if (!issue) return null;
    
    return this.issueRepo.updateStatus(issue.id, IssueStatus.Paused);
  }

  resume(projectId: string, number: number): Issue | null {
    const issue = this.issueRepo.findByNumber(projectId, number);
    if (!issue) return null;
    if (issue.status === IssueStatus.Completed) return null;
    if (issue.status === IssueStatus.Closed) return null;
    
    return this.issueRepo.updateStatus(issue.id, IssueStatus.Active);
  }

  reopen(projectId: string, number: number): Issue | null {
    const issue = this.issueRepo.findByNumber(projectId, number);
    if (!issue) return null;
    if (issue.status !== IssueStatus.Closed && issue.status !== IssueStatus.Blocked && issue.status !== IssueStatus.Paused && issue.status !== IssueStatus.Interrupted) return null;
    
    this.issueRepo.clearApprovalState(issue.id);
    return this.issueRepo.updateStatus(issue.id, IssueStatus.Active);
  }

  block(projectId: string, number: number, reason?: string): Issue | null {
    const issue = this.issueRepo.findByNumber(projectId, number);
    if (!issue) return null;

    if (reason) {
      return this.issueRepo.blockIssue(issue.id, reason);
    }
    return this.issueRepo.updateStatus(issue.id, IssueStatus.Blocked);
  }

  close(projectId: string, number: number): Issue | null {
    const issue = this.issueRepo.findByNumber(projectId, number);
    if (!issue) return null;
    
    return this.issueRepo.updateStatus(issue.id, IssueStatus.Closed);
  }

  update(issueId: string, data: Partial<{ title: string; body: string; stage: Stage; status: IssueStatus; labels: string[]; priority: Priority; model: string | null }>): Issue | null {
    return this.issueRepo.update(issueId, data);
  }

  createComment(issueId: string, body: string): Comment {
    return this.commentRepo.create({ issueId, body });
  }

  getCommentsByIssue(issueId: string): Comment[] {
    return this.commentRepo.findByIssue(issueId);
  }

  delete(issueId: string): boolean {
    return this.issueRepo.deleteCascade(issueId);
  }

  deleteByProject(projectId: string): number {
    return this.issueRepo.deleteByProjectCascade(projectId);
  }

  count(projectId: string): number {
    return this.issueRepo.count(projectId);
  }

  async archive(projectId: string, number: number, options?: ArchiveOptions): Promise<ArchiveResult> {
    const issue = this.issueRepo.findByNumber(projectId, number);
    if (!issue) {
      throw new Error(`Issue #${number} not found`);
    }

    if (this.agentRunner?.isRunning(issue.id)) {
      throw new Error('Cannot archive: issue has a running agent. Force-stop it first.');
    }

    const warning = issue.stage !== Stage.Done
      ? `Warning: Issue #${number} is not completed (stage: ${issue.stage}). Archived anyway.`
      : undefined;

    const updated = this.issueRepo.archive(issue.id);
    if (!updated) {
      throw new Error(`Failed to archive issue #${number}`);
    }

    const cleanup = options?.cleanup !== false;
    if (cleanup) {
      await this.performCleanup(projectId, number);
    }

    log.info('Issue archived', { issueNumber: number, cleanup });

    return { issue: updated, warning };
  }

  async unarchive(projectId: string, number: number): Promise<Issue> {
    const issue = this.issueRepo.findByNumber(projectId, number);
    if (!issue) {
      throw new Error(`Issue #${number} not found`);
    }

    const updated = this.issueRepo.unarchive(issue.id);
    if (!updated) {
      throw new Error(`Failed to unarchive issue #${number}`);
    }

    const project = this.projectRepo?.findById(projectId);
    if (project) {
      const artifactsManager = new ChangeArtifactsManager(project.path);
      try {
        await artifactsManager.restoreChange(number);
      } catch {
        // archived change dir doesn't exist, skip gracefully
      }
    }

    log.info('Issue unarchived', { issueNumber: number });

    return updated;
  }

  async archiveAllCompleted(projectId: string): Promise<ArchiveAllResult> {
    const completed = this.issueRepo.findAll({
      projectId,
      stage: Stage.Done,
      includeArchived: false,
    });

    if (completed.length === 0) {
      return { count: 0, message: 'No completed issues to archive.' };
    }

    let count = 0;
    for (const issue of completed) {
      try {
        await this.archive(projectId, issue.number);
        count++;
      } catch (err) {
        log.warn('Failed to archive issue in batch', {
          issueNumber: issue.number,
          error: err instanceof Error ? err.message : String(err),
        });
      }
    }

    return { count, message: `Archived ${count} issues.` };
  }

  private async performCleanup(projectId: string, issueNumber: number): Promise<void> {
    const project = this.projectRepo?.findById(projectId);

    if (project && this.worktreeManager) {
      try {
        await this.worktreeManager.remove(project.path, project.name, issueNumber);
      } catch (err) {
        log.warn('Failed to remove worktree during archive', {
          issueNumber,
          error: err instanceof Error ? err.message : String(err),
        });
      }
    }

    if (project) {
      const artifactsManager = new ChangeArtifactsManager(project.path);
      try {
        await artifactsManager.archiveChange(issueNumber);
      } catch {
        // change dir doesn't exist, skip gracefully
      }
    }

    if (this.checkpointRepo) {
      try {
        this.checkpointRepo.deleteAll(issueNumber);
      } catch (err) {
        log.warn('Failed to cleanup checkpoints during archive', {
          issueNumber,
          error: err instanceof Error ? err.message : String(err),
        });
      }
    }
  }
}
