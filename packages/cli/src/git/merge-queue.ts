import { execFile } from 'child_process';
import { promisify } from 'util';
import { WorktreeManager } from './worktree-manager';
import { EventBus } from '../services/event-bus';
import { IssueRepo } from '../db/issue-repo';
import { MergeState } from '../types';
import { Log } from '../util/log';

const log = Log.create({ service: 'merge-queue' });

const execFileAsync = promisify(execFile);

const BUILD_TIMEOUT_MS = 5 * 60 * 1000;

export interface MergeEntry {
  issueNumber: number;
  projectId: string;
  issueId: string;
  mergeState: MergeState;
  message?: string;
  conflictingFiles?: string[];
  enqueuedAt: number;
}

interface MergeQueueDeps {
  worktreeManager: WorktreeManager;
  eventBus: EventBus;
  issueRepo: IssueRepo;
  getProjectPath: (projectId: string) => { path: string; name: string; baseBranch: string } | null;
}

export class MergeQueue {
  private queue = new Map<number, MergeEntry>();
  private processing = false;
  private deps: MergeQueueDeps;

  constructor(deps: MergeQueueDeps) {
    this.deps = deps;
  }

  enqueue(projectId: string, issueNumber: number): void {
    if (this.queue.has(issueNumber)) {
      log.warn('Duplicate enqueue, ignoring', { issueNumber, projectId });
      return;
    }

    const issue = this.deps.issueRepo.findByNumber(projectId, issueNumber);
    if (!issue) {
      log.warn('Issue not found for enqueue', { issueNumber, projectId });
      return;
    }

    const entry: MergeEntry = {
      issueNumber,
      projectId,
      issueId: issue.id,
      mergeState: 'pending' as MergeState,
      enqueuedAt: Date.now(),
    };

    this.queue.set(issueNumber, entry);
    this.deps.issueRepo.setMergeState(issue.id, 'pending');

    const position = this.getPendingCount();
    this.deps.eventBus.emit('merge_queued', {
      issueId: issue.id,
      projectId,
      issueNumber,
      position,
    });

    log.info('Issue enqueued for merge', { issueNumber, projectId, position });

    this.processNext();
  }

  retry(issueNumber: number): boolean {
    const entry = this.queue.get(issueNumber);
    if (!entry) {
      log.warn('retry: entry not found in queue', { issueNumber });
      return false;
    }

    if (entry.mergeState !== 'build-failed' && entry.mergeState !== 'conflict' && entry.mergeState !== 'blocked') {
      log.warn('retry: invalid state', { issueNumber, mergeState: entry.mergeState });
      return false;
    }

    entry.mergeState = 'pending';
    entry.message = undefined;
    entry.enqueuedAt = Date.now();

    this.deps.issueRepo.setMergeState(entry.issueId, 'pending');

    log.info('Issue re-enqueued for retry', { issueNumber, projectId: entry.projectId });

    this.processNext();
    return true;
  }

  getStatus(): MergeEntry[] {
    return Array.from(this.queue.values()).sort(
      (a, b) => a.enqueuedAt - b.enqueuedAt
    );
  }

  recoverFromDB(): void {
    const issues = this.deps.issueRepo.findByMergeStates(['pending', 'merging', 'rebasing', 'blocked']);

    for (const issue of issues) {
      if (this.queue.has(issue.number)) continue;

      const mergeState: MergeState = issue.mergeState === 'merging' ? 'pending' : 'pending';

      this.deps.issueRepo.setMergeState(issue.id, mergeState);

      const entry: MergeEntry = {
        issueNumber: issue.number,
        projectId: issue.projectId,
        issueId: issue.id,
        mergeState: 'pending',
        enqueuedAt: Date.now(),
      };

      this.queue.set(issue.number, entry);
      log.info('Recovered issue from DB into merge queue', {
        issueNumber: issue.number,
        projectId: issue.projectId,
        previousState: issue.mergeState,
      });
    }

    if (this.queue.size > 0) {
      log.info('Merge queue recovery complete', { recoveredCount: this.queue.size });
      this.processNext();
    }
  }

  private getPendingCount(): number {
    let count = 0;
    for (const entry of this.queue.values()) {
      if (entry.mergeState === 'pending') count++;
    }
    return count;
  }

  private async processNext(): Promise<void> {
    if (this.processing) return;

    const entry = this.pickNext();
    if (!entry) return;

    this.processing = true;

    try {
      await this.processItem(entry);
    } catch (err) {
      log.error('Unexpected error in processItem', {
        issueNumber: entry.issueNumber,
        error: err instanceof Error ? err.message : String(err),
      });
    } finally {
      this.processing = false;
      this.processNext();
    }
  }

  private pickNext(): MergeEntry | null {
    let earliest: MergeEntry | null = null;
    for (const entry of this.queue.values()) {
      if (entry.mergeState !== 'pending') continue;
      if (!earliest || entry.enqueuedAt < earliest.enqueuedAt) {
        earliest = entry;
      }
    }
    return earliest;
  }

  private async processItem(entry: MergeEntry): Promise<void> {
    entry.mergeState = 'merging';
    this.deps.issueRepo.setMergeState(entry.issueId, 'merging');

    this.deps.eventBus.emit('merge_started', {
      issueId: entry.issueId,
      projectId: entry.projectId,
      issueNumber: entry.issueNumber,
    });

    log.info('Processing merge', { issueNumber: entry.issueNumber, projectId: entry.projectId });

    const project = this.deps.getProjectPath(entry.projectId);
    if (!project) {
      this.handleFailure(entry, 'Project not found');
      return;
    }

    try {
      await execFileAsync('git', ['merge', '--abort'], { cwd: project.path, timeout: 10000 }).catch(() => {});
    } catch {}

    const mergeResult = await this.deps.worktreeManager.mergeBack(
      project.path,
      project.name,
      entry.issueNumber,
      project.baseBranch,
    );

    if (!mergeResult.success) {
      const isConflict = mergeResult.message.toLowerCase().includes('conflict');
      this.handleFailure(entry, mergeResult.message, isConflict ? 'conflict' : 'build-failed');
      return;
    }

    log.info('Merge succeeded, running build verification', {
      issueNumber: entry.issueNumber,
      projectPath: project.path,
    });

    const buildOk = await this.runBuildVerification(project.path);
    if (!buildOk) {
      log.warn('Build verification failed, rolling back merge', {
        issueNumber: entry.issueNumber,
      });
      await this.rollbackMerge(project.path);
      this.handleFailure(entry, 'Build verification failed (npm run build)');
      return;
    }

    log.info('Build verification passed, cleaning up worktree', {
      issueNumber: entry.issueNumber,
    });

    try {
      await this.deps.worktreeManager.remove(project.path, project.name, entry.issueNumber);
    } catch (err) {
      log.warn('Failed to clean up worktree after successful merge', {
        issueNumber: entry.issueNumber,
        error: err instanceof Error ? err.message : String(err),
      });
    }

    entry.mergeState = 'merged';
    this.deps.issueRepo.setMergeState(entry.issueId, 'merged');

    this.deps.eventBus.emit('merge_completed', {
      issueId: entry.issueId,
      projectId: entry.projectId,
      issueNumber: entry.issueNumber,
    });

    log.info('Merge completed successfully', { issueNumber: entry.issueNumber });
  }

  private handleFailure(
    entry: MergeEntry,
    message: string,
    state: MergeState = 'build-failed',
  ): void {
    entry.mergeState = state;
    entry.message = message;
    this.deps.issueRepo.setMergeState(entry.issueId, state);

    this.deps.eventBus.emit('merge_failed', {
      issueId: entry.issueId,
      projectId: entry.projectId,
      issueNumber: entry.issueNumber,
      reason: state === 'conflict' ? 'conflict' : 'build-failed',
    });

    log.warn('Merge failed', {
      issueNumber: entry.issueNumber,
      state,
      message,
    });
  }

  private async runBuildVerification(projectPath: string): Promise<boolean> {
    try {
      const { stdout, stderr } = await execFileAsync('npm', ['run', 'build'], {
        cwd: projectPath,
        timeout: BUILD_TIMEOUT_MS,
        maxBuffer: 10 * 1024 * 1024,
      });
      log.debug('Build verification output', {
        stdout: stdout?.slice(0, 500),
        stderr: stderr?.slice(0, 500),
      });
      return true;
    } catch (err: any) {
      log.warn('Build verification failed', {
        error: err.message || String(err),
        killed: err.killed || false,
        code: err.code,
      });
      return false;
    }
  }

  private async rollbackMerge(projectPath: string): Promise<void> {
    try {
      await execFileAsync('git', ['reset', '--hard', 'HEAD~1'], {
        cwd: projectPath,
        timeout: 30000,
      });
      log.info('Rolled back merge commit', { projectPath });
    } catch (err) {
      log.error('Failed to rollback merge', {
        projectPath,
        error: err instanceof Error ? err.message : String(err),
      });
    }
  }
}
