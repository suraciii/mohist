import * as fs from 'fs';
import * as path from 'path';
import type { Check, CheckContext, CheckResult } from '@mohist/workflow/checks';

export class AllTasksCompleteCheck implements Check {
  public readonly name = 'all-tasks-complete';

  async run(ctx: CheckContext): Promise<CheckResult> {
    if (!ctx.changeDir) {
      return { name: this.name, status: 'fail', message: 'No change directory' };
    }

    const tasksPath = path.join(ctx.changeDir, 'tasks.json');

    if (!fs.existsSync(tasksPath)) {
      return { name: this.name, status: 'fail', message: 'tasks.json not found' };
    }

    try {
      const content = fs.readFileSync(tasksPath, 'utf-8');
      const parsed = JSON.parse(content);

      if (!parsed.tasks || !Array.isArray(parsed.tasks)) {
        return { name: this.name, status: 'fail', message: 'tasks.json has no valid tasks array' };
      }

      const tasks = parsed.tasks;

      if (tasks.length === 0) {
        return { name: this.name, status: 'fail', message: 'No tasks defined in tasks.json' };
      }

      const failed = tasks.filter((t: any) => !t.passes);
      if (failed.length > 0) {
        const failedIds = failed.map((t: any) => t.id).join(', ');
        return {
          name: this.name,
          status: 'fail',
          message: `${failed.length} task(s) not complete: ${failedIds}`,
        };
      }

      return {
        name: this.name,
        status: 'pass',
        message: `All ${tasks.length} task(s) complete`,
      };
    } catch (err: any) {
      return {
        name: this.name,
        status: 'fail',
        message: `Failed to parse tasks.json: ${err.message}`,
      };
    }
  }
}
