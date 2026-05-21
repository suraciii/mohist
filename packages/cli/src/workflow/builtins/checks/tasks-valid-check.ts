import * as fs from 'fs';
import * as path from 'path';
import type { Check, CheckContext, CheckResult } from '@mohist/workflow/checks';

export class TasksValidCheck implements Check {
  public readonly name = 'tasks-valid';

  async run(ctx: CheckContext): Promise<CheckResult> {
    if (!ctx.changeDir) {
      return { name: this.name, status: 'fail', message: 'No change directory' };
    }
    const filePath = path.join(ctx.changeDir, 'tasks.json');
    if (!fs.existsSync(filePath)) {
      return { name: this.name, status: 'fail', message: 'tasks.json not found' };
    }
    try {
      const content = fs.readFileSync(filePath, 'utf-8');
      const parsed = JSON.parse(content);
      if (!parsed.tasks || !Array.isArray(parsed.tasks) || parsed.tasks.length === 0) {
        return { name: this.name, status: 'fail', message: 'tasks.json has no tasks array or is empty' };
      }
      return { name: this.name, status: 'pass', message: `tasks.json valid with ${parsed.tasks.length} task(s)` };
    } catch {
      return { name: this.name, status: 'fail', message: 'tasks.json is not valid JSON' };
    }
  }
}
