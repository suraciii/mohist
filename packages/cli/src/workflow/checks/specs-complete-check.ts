import * as fs from 'fs';
import * as path from 'path';
import type { Check, CheckContext, CheckResult } from './index';
import type { ReactionConfig } from '../stage-context';

const ARTIFACT_REACTION: ReactionConfig = {
  type: 'retry-task',
  maxAttempts: 3,
  fallbackReaction: { type: 'escalate' },
};

export class SpecsCompleteCheck implements Check {
  public readonly name = 'specs-complete';
  public readonly reaction: ReactionConfig = ARTIFACT_REACTION;

  async run(ctx: CheckContext): Promise<CheckResult> {
    if (!ctx.changeDir) {
      return { name: this.name, status: 'fail', message: 'No change directory' };
    }
    const specsDir = path.join(ctx.changeDir, 'specs');
    if (!fs.existsSync(specsDir)) {
      return { name: this.name, status: 'fail', message: 'specs/ directory not found' };
    }
    if (!fs.statSync(specsDir).isDirectory()) {
      return { name: this.name, status: 'fail', message: 'specs/ is not a directory' };
    }
    const entries = fs.readdirSync(specsDir, { recursive: true, encoding: 'utf-8' });
    const mdFiles = entries.filter((e): e is string => typeof e === 'string' && e.endsWith('.md'));
    if (mdFiles.length === 0) {
      return { name: this.name, status: 'fail', message: 'specs/ directory contains no .md files' };
    }
    const allNonEmpty = mdFiles.every(f => {
      const fp = path.join(specsDir, f);
      return fs.statSync(fp).size > 0;
    });
    if (!allNonEmpty) {
      return { name: this.name, status: 'fail', message: 'Some spec files are empty' };
    }
    return { name: this.name, status: 'pass', message: `specs/ has ${mdFiles.length} spec file(s)` };
  }
}
