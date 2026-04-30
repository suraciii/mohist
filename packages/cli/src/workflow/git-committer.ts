import { execFile } from 'child_process';
import { promisify } from 'util';
import type { Issue } from '../types';
import { Log } from '../util/log';

const execFileAsync = promisify(execFile);

const log = Log.create({ service: 'workflow' });

export class GitCommitter {
  constructor(private readonly worktreePath: string) {}

  async commitBuildChanges(issue: Issue): Promise<void> {
    try {
      const { stdout: statusOut } = await execFileAsync(
        'git',
        ['status', '--porcelain', '--ignore-submodules'],
        { cwd: this.worktreePath },
      );

      const lines = statusOut
        .split('\n')
        .filter(l => l.trim() !== '')
        .filter(l => !l.endsWith('openspec/changes/') && !l.includes('openspec/changes/'));

      if (lines.length === 0) {
        log.info('No changes to commit after build stage', { issueNumber: issue.number });
        return;
      }

      await execFileAsync(
        'git',
        ['add', '--', ':!openspec/changes/', ':!.opencode/'],
        { cwd: this.worktreePath },
      );

      const message = `build(issue-${issue.number}): ${issue.title}`;
      await execFileAsync('git', ['commit', '-m', message, '--no-verify'], {
        cwd: this.worktreePath,
      });

      log.info('Build stage changes committed', { issueNumber: issue.number, files: lines.length });
    } catch (err) {
      log.warn('Failed to commit build stage changes', {
        issueNumber: issue.number,
        error: err instanceof Error ? err.message : String(err),
      });
    }
  }
}
