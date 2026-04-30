import type { Check, CheckContext, CheckResult } from './index';
import type { WorktreeManager, ProjectRepo } from '../stage-context';
import { Log } from '../../util/log';

const log = Log.create({ service: 'merge-ready-check' });

export interface MergeReadyCheckOptions {
  worktreeManager?: WorktreeManager;
  projectRepo?: ProjectRepo;
}

export class MergeReadyCheck implements Check {
  public readonly name = 'merge-ready';
  private worktreeManager?: WorktreeManager;
  private projectRepo?: ProjectRepo;

  constructor(options: MergeReadyCheckOptions) {
    this.worktreeManager = options.worktreeManager;
    this.projectRepo = options.projectRepo;
  }

  async run(ctx: CheckContext): Promise<CheckResult> {
    const startTime = Date.now();

    if (!this.worktreeManager || !this.projectRepo) {
      const duration = Date.now() - startTime;
      log.warn('Merge Ready check skipped: worktreeManager or projectRepo not available', {
        issueNumber: ctx.issue.number,
      });
      return {
        name: this.name,
        status: 'pass',
        message: 'Merge Ready: skipped (worktreeManager or projectRepo not configured)',
        output: { duration },
      };
    }

    const project = this.projectRepo.findById(ctx.issue.projectId);
    if (!project) {
      const duration = Date.now() - startTime;
      log.warn('Merge Ready check skipped: project not found', {
        issueNumber: ctx.issue.number,
        projectId: ctx.issue.projectId,
      });
      return {
        name: this.name,
        status: 'pass',
        message: 'Merge Ready: skipped (project not found)',
        output: { duration },
      };
    }

    try {
      const canFF = await this.worktreeManager.canFastForward(
        project.path,
        project.name,
        ctx.issue.number,
        project.baseBranch,
      );
      const duration = Date.now() - startTime;

      return {
        name: this.name,
        status: 'pass',
        message: canFF ? 'Merge Ready: yes' : 'Merge Ready: needs rebase',
        output: { duration, canFastForward: canFF },
      };
    } catch (err) {
      const duration = Date.now() - startTime;
      log.warn('Merge Ready check error', {
        issueNumber: ctx.issue.number,
        error: err instanceof Error ? err.message : String(err),
      });
      return {
        name: this.name,
        status: 'pass',
        message: `Merge Ready: check error (${err instanceof Error ? err.message : String(err)})`,
        output: { duration },
      };
    }
  }
}
