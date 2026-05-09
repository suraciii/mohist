import type { Check, CheckContext, CheckResult } from './index';
import { OpenSpecIntegrator } from '../../openspec/open-spec-integrator';

export class OpenSpecSyncDryRunCheck implements Check {
  public readonly name = 'openspec-sync-dry-run';

  async run(ctx: CheckContext): Promise<CheckResult> {
    const changeDir = ctx.changeDir;
    if (!changeDir) {
      return {
        name: this.name,
        status: 'error',
        message: 'Change directory not found',
      };
    }

    const worktreePath = ctx.acpOptions?.cwd ?? changeDir;
    const integrator = new OpenSpecIntegrator();

    try {
      const summary = await integrator.preview(changeDir, worktreePath);

      return {
        name: this.name,
        status: summary.valid ? 'pass' : 'fail',
        message: summary.valid
          ? `OpenSpec sync dry-run passed — ${summary.capabilities.length} capability(ies) touched`
          : `OpenSpec sync dry-run failed — ${summary.conflicts.length} conflict(s)`,
        output: {
          kind: 'openspec-sync-dry-run',
          capabilities: summary.capabilities,
          targetFiles: summary.targetFiles,
          counts: {
            added: summary.added,
            modified: summary.modified,
            removed: summary.removed,
            renamed: summary.renamed,
          },
          conflicts: summary.conflicts.map(c => ({
            capability: c.capability,
            type: c.type,
            detail: c.detail,
            requirementHeader: c.requirementHeader,
          })),
          valid: summary.valid,
        },
      };
    } catch (err) {
      return {
        name: this.name,
        status: 'error',
        message: `OpenSpec sync dry-run error: ${err instanceof Error ? err.message : String(err)}`,
      };
    }
  }
}