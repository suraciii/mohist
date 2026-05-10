import { execFile } from 'child_process';
import * as path from 'path';
import { promisify } from 'util';
import { WorktreeManager, type MergeMetadata } from './worktree-manager';
import { EventBus } from '../services/event-bus';
import { IssueRepo } from '../db/issue-repo';
import { MergeState } from '../types';
import { Log } from '../util/log';
import type { PostMergeFinalizer } from '../services/post-merge-finalizer';

const log = Log.create({ service: 'merge-queue' });

const execFileAsync = promisify(execFile);

const BUILD_TIMEOUT_MS = 5 * 60 * 1000;
const MAX_BUILD_FIX_ATTEMPTS = 2;

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
  isAgentRunning?: (issueNumber: number) => boolean;
  getProjectPath: (projectId: string) => { path: string; name: string; baseBranch: string } | null;
  resolveConflicts: (entry: MergeEntry, worktreePath: string, conflictFiles: string[]) => Promise<{ success: boolean; error?: string }>;
  fixBuildErrors: (entry: MergeEntry, worktreePath: string, buildOutput: string) => Promise<{ success: boolean; error?: string }>;
  postMergeFinalizer?: PostMergeFinalizer;
  getMergeMetadata: (projectId: string, issueNumber: number) => Promise<MergeMetadata | undefined>;
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
      mergeState: MergeState.Pending,
      enqueuedAt: Date.now(),
    };

    this.queue.set(issueNumber, entry);
    this.deps.issueRepo.setMergeState(issue.id, MergeState.Pending);

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
    let entry = this.queue.get(issueNumber);

    if (!entry) {
      const dbIssue = this.deps.issueRepo.findByMergeStates([
        MergeState.BuildFailed,
        MergeState.Conflict,
        MergeState.Blocked,
      ]).find(i => i.number === issueNumber);

      if (!dbIssue) {
        log.warn('retry: issue not found in queue or DB', { issueNumber });
        return false;
      }

      entry = {
        issueNumber: dbIssue.number,
        projectId: dbIssue.projectId,
        issueId: dbIssue.id,
        mergeState: dbIssue.mergeState as MergeState,
        enqueuedAt: Date.now(),
      };

      this.queue.set(issueNumber, entry);
      log.info('retry: recovered entry from DB', { issueNumber, previousState: dbIssue.mergeState });
    }

    if (entry.mergeState !== MergeState.BuildFailed && entry.mergeState !== MergeState.Conflict && entry.mergeState !== MergeState.Blocked) {
      log.warn('retry: invalid state', { issueNumber, mergeState: entry.mergeState });
      return false;
    }

    entry.mergeState = MergeState.Pending;
    entry.message = undefined;
    entry.enqueuedAt = Date.now();

    this.deps.issueRepo.setMergeState(entry.issueId, MergeState.Pending);

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
    const issues = this.deps.issueRepo.findByMergeStates([
      MergeState.Pending,
      MergeState.Merging,
      MergeState.Rebasing,
      MergeState.Resolving,
      MergeState.Blocked,
    ]);

    for (const issue of issues) {
      if (this.queue.has(issue.number)) continue;

      if (this.deps.isAgentRunning?.(issue.number)) {
        log.info('Skipping recovery: agent is running for issue', { issueNumber: issue.number });
        continue;
      }

      this.deps.issueRepo.setMergeState(issue.id, MergeState.Pending);

      const entry: MergeEntry = {
        issueNumber: issue.number,
        projectId: issue.projectId,
        issueId: issue.id,
        mergeState: MergeState.Pending,
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
      if (entry.mergeState === MergeState.Pending) count++;
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
      if (entry.mergeState !== MergeState.Pending) continue;
      if (!earliest || entry.enqueuedAt < earliest.enqueuedAt) {
        earliest = entry;
      }
    }
    return earliest;
  }

  private async processItem(entry: MergeEntry): Promise<void> {
    entry.mergeState = MergeState.Rebasing;
    this.deps.issueRepo.setMergeState(entry.issueId, MergeState.Rebasing);

    this.deps.eventBus.emit('merge_started', {
      issueId: entry.issueId,
      projectId: entry.projectId,
      issueNumber: entry.issueNumber,
    });

    log.info('Processing merge (rebase-first)', { issueNumber: entry.issueNumber, projectId: entry.projectId });

    const project = this.deps.getProjectPath(entry.projectId);
    if (!project) {
      this.handleFailure(entry, 'Project not found');
      return;
    }

    const canFF = await this.deps.worktreeManager.canFastForward(
      project.path,
      project.name,
      entry.issueNumber,
      project.baseBranch,
    );

    let rebased = false;

    if (!canFF) {
      const rebaseResult = await this.deps.worktreeManager.rebaseOntoMaster(
        project.path,
        project.name,
        entry.issueNumber,
        project.baseBranch,
        { abortOnConflict: false },
      );

      if (!rebaseResult.success) {
        const resolved = await this.handleConflictResolution(entry, project, rebaseResult.conflicts);
        if (!resolved) return;
      }

      rebased = true;

      const canFFAfterRebase = await this.deps.worktreeManager.canFastForward(
        project.path,
        project.name,
        entry.issueNumber,
        project.baseBranch,
      );

      if (!canFFAfterRebase) {
        this.handleFailure(entry, 'Rebase succeeded but branch still cannot fast-forward');
        return;
      }
    }

    if (rebased) {
      const worktreePath = this.deps.worktreeManager.getPath(project.name, entry.issueNumber);
      if (!worktreePath) {
        this.handleFailure(entry, 'Worktree not found for build verification');
        return;
      }

      log.info('Rebase changed code, running build verification', {
        issueNumber: entry.issueNumber,
        worktreePath,
      });

      const buildResult = await this.runBuildVerificationWithFix(entry, worktreePath);
      if (!buildResult) {
        return;
      }

      log.info('Build verification passed after rebase', {
        issueNumber: entry.issueNumber,
      });
    } else {
      log.info('Branch already fast-forwardable, skipping build verification', {
        issueNumber: entry.issueNumber,
      });
    }

    log.info('Performing squash merge', {
      issueNumber: entry.issueNumber,
    });

    entry.mergeState = MergeState.Merging;
    this.deps.issueRepo.setMergeState(entry.issueId, MergeState.Merging);

    const mergeMetadata = await this.deps.getMergeMetadata(entry.projectId, entry.issueNumber);
    if (!mergeMetadata) {
      this.handleFailure(entry, `Merge metadata not found for issue #${entry.issueNumber}`);
      return;
    }

    const mergeResult = await this.deps.worktreeManager.mergeBack(
      project.path,
      project.name,
      entry.issueNumber,
      project.baseBranch,
      mergeMetadata,
    );

    if (!mergeResult.success) {
      const isConflict = mergeResult.message.toLowerCase().includes('conflict');
      this.handleFailure(entry, mergeResult.message, isConflict ? MergeState.Conflict : MergeState.BuildFailed);
      return;
    }

    log.info('Squash merge succeeded; retaining worktree until archive', {
      issueNumber: entry.issueNumber,
    });

    const issue = this.deps.issueRepo.findById(entry.issueId);
    if (!issue) {
      this.handleFailure(entry, 'Issue not found for post-merge finalization');
      return;
    }

    if (!this.deps.postMergeFinalizer) {
      this.handleFailure(entry, 'PostMergeFinalizer not configured');
      return;
    }

    const finalization = await this.deps.postMergeFinalizer.finalize(issue);
    if (!finalization.success) {
      this.handleFailure(entry, finalization.error || 'Post-merge health gate failed');
      return;
    }

    entry.mergeState = MergeState.Merged;

    this.queue.delete(entry.issueNumber);

    this.deps.eventBus.emit('merge_completed', {
      issueId: entry.issueId,
      projectId: entry.projectId,
      issueNumber: entry.issueNumber,
    });

    log.info('Merge completed successfully', { issueNumber: entry.issueNumber });
  }

  private isStructuredMergeFailure(result: MergeBackResult): result is Extract<MergeBackResult, { success: false }> {
    return result.success === false;
  }

  private async handleConflictResolution(
    entry: MergeEntry,
    project: { path: string; name: string; baseBranch: string },
    conflictFiles: string[],
  ): Promise<boolean> {
    const worktreePath = this.deps.worktreeManager.getPath(project.name, entry.issueNumber);
    if (!worktreePath) {
      this.handleFailure(entry, 'Worktree not found for conflict resolution', MergeState.Conflict);
      return false;
    }

    entry.mergeState = MergeState.Resolving;
    entry.conflictingFiles = conflictFiles;
    this.deps.issueRepo.setMergeState(entry.issueId, MergeState.Resolving);

    this.deps.eventBus.emit('agent_conflict_resolution_started', {
      issueId: entry.issueId,
      projectId: entry.projectId,
      issueNumber: entry.issueNumber,
      conflictFiles,
    });

    log.info('Delegating conflict resolution to agent', {
      issueNumber: entry.issueNumber,
      conflictFiles,
    });

    const result = await this.deps.resolveConflicts(entry, worktreePath, conflictFiles);

    if (!result.success) {
      this.handleFailure(entry, result.error || 'Agent conflict resolution failed', MergeState.Conflict);
      try {
        await this.deps.worktreeManager.abortRebase(project.name, entry.issueNumber);
      } catch (err) {
        log.warn('Failed to abort rebase after failed resolution', {
          issueNumber: entry.issueNumber,
          error: err instanceof Error ? err.message : String(err),
        });
      }
      return false;
    }

    const rebaseDone = !(await this.deps.worktreeManager.isRebaseInProgress(project.name, entry.issueNumber));
    if (!rebaseDone) {
      this.handleFailure(entry, 'Agent returned success but rebase is still in progress', MergeState.Conflict);
      try {
        await this.deps.worktreeManager.abortRebase(project.name, entry.issueNumber);
      } catch (err) {
        log.warn('Failed to abort rebase after incomplete resolution', {
          issueNumber: entry.issueNumber,
          error: err instanceof Error ? err.message : String(err),
        });
      }
      return false;
    }

    this.deps.eventBus.emit('agent_conflict_resolution_completed', {
      issueId: entry.issueId,
      projectId: entry.projectId,
      issueNumber: entry.issueNumber,
    });

    log.info('Agent conflict resolution succeeded, rebase completed', {
      issueNumber: entry.issueNumber,
    });

    return true;
  }

  private handleFailure(
    entry: MergeEntry,
    message: string,
    state: MergeState = MergeState.BuildFailed,
  ): void {
    entry.mergeState = state;
    entry.message = message;
    this.deps.issueRepo.setMergeState(entry.issueId, state);

    this.deps.eventBus.emit('merge_failed', {
      issueId: entry.issueId,
      projectId: entry.projectId,
      issueNumber: entry.issueNumber,
      reason: state === MergeState.Conflict ? MergeState.Conflict : MergeState.BuildFailed,
    });

    log.warn('Merge failed', {
      issueNumber: entry.issueNumber,
      state,
      message,
    });
  }

  private async runBuildVerification(worktreePath: string): Promise<{ ok: boolean; output: string }> {
    const buildPath = path.join(worktreePath, 'packages', 'cli');
    try {
      const { stdout, stderr } = await execFileAsync('npm', ['run', 'build'], {
        cwd: buildPath,
        timeout: BUILD_TIMEOUT_MS,
        maxBuffer: 10 * 1024 * 1024,
      });
      log.debug('Build verification output', {
        stdout: stdout?.slice(0, 500),
        stderr: stderr?.slice(0, 500),
      });
      return { ok: true, output: '' };
    } catch (err: any) {
      const output = [err.stdout, err.stderr, err.message].filter(Boolean).join('\n');
      log.warn('Build verification failed', {
        error: err.message || String(err),
        killed: err.killed || false,
        code: err.code,
      });
      return { ok: false, output };
    }
  }

  private async runBuildVerificationWithFix(entry: MergeEntry, worktreePath: string): Promise<boolean> {
    let attempt = 0;

    while (attempt <= MAX_BUILD_FIX_ATTEMPTS) {
      const result = await this.runBuildVerification(worktreePath);
      if (result.ok) return true;

      if (attempt >= MAX_BUILD_FIX_ATTEMPTS) {
        this.handleFailure(entry, `Build verification failed after ${attempt} fix attempt(s)`, MergeState.BuildFailed);
        return false;
      }

      log.info('Build failed, delegating fix to coder agent', {
        issueNumber: entry.issueNumber,
        attempt: attempt + 1,
        maxAttempts: MAX_BUILD_FIX_ATTEMPTS,
      });

      entry.mergeState = MergeState.BuildFailed;
      this.deps.issueRepo.setMergeState(entry.issueId, MergeState.BuildFailed);

      this.deps.eventBus.emit('agent_build_fix_started', {
        issueId: entry.issueId,
        projectId: entry.projectId,
        issueNumber: entry.issueNumber,
        attempt: attempt + 1,
      });

      const fixResult = await this.deps.fixBuildErrors(entry, worktreePath, result.output);

      if (!fixResult.success) {
        this.handleFailure(entry, fixResult.error || 'Coder agent build fix failed', MergeState.BuildFailed);
        return false;
      }

      this.deps.eventBus.emit('agent_build_fix_completed', {
        issueId: entry.issueId,
        projectId: entry.projectId,
        issueNumber: entry.issueNumber,
        attempt: attempt + 1,
      });

      log.info('Coder agent finished build fix, re-running verification', {
        issueNumber: entry.issueNumber,
        attempt: attempt + 1,
      });

      attempt++;
    }

    this.handleFailure(entry, 'Build verification failed', MergeState.BuildFailed);
    return false;
  }

}
