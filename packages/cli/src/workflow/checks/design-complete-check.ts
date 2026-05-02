import * as fs from 'fs';
import * as path from 'path';
import type { Check, CheckContext, CheckResult } from './index';
import type { ReactionConfig } from '../stage-context';

const ARTIFACT_REACTION: ReactionConfig = {
  type: 'retry-task',
  maxAttempts: 3,
  fallbackReaction: { type: 'escalate' },
};

export class DesignCompleteCheck implements Check {
  public readonly name = 'design-complete';
  public readonly reaction: ReactionConfig = ARTIFACT_REACTION;

  async run(ctx: CheckContext): Promise<CheckResult> {
    if (!ctx.changeDir) {
      return { name: this.name, status: 'fail', message: 'No change directory' };
    }
    const filePath = path.join(ctx.changeDir, 'design.md');
    if (!fs.existsSync(filePath)) {
      return { name: this.name, status: 'fail', message: 'design.md not found' };
    }
    const stat = fs.statSync(filePath);
    if (stat.size === 0) {
      return { name: this.name, status: 'fail', message: 'design.md is empty' };
    }
    return { name: this.name, status: 'pass', message: 'design.md exists and is non-empty' };
  }
}
