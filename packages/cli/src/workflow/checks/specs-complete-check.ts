import * as fs from 'fs';
import * as path from 'path';
import type { Check, CheckContext, CheckResult } from './index';

export class SpecsCompleteCheck implements Check {
  public readonly name = 'specs-complete';

  async run(ctx: CheckContext): Promise<CheckResult> {
    if (!ctx.changeDir) {
      return { name: this.name, status: 'fail', message: 'No change directory' };
    }
    const specsDir = path.join(ctx.changeDir, 'specs');
    if (!fs.existsSync(specsDir) || !fs.statSync(specsDir).isDirectory()) {
      return { name: this.name, status: 'pass', message: 'No specs/ directory — skipping spec validation' };
    }
    const entries = fs.readdirSync(specsDir, { recursive: true, encoding: 'utf-8' });
    const mdFiles = entries.filter((e): e is string => typeof e === 'string' && e.endsWith('.md'));
    if (mdFiles.length === 0) {
      return { name: this.name, status: 'pass', message: 'specs/ directory is empty — no specs to validate' };
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
