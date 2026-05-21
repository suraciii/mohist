import type { WorkflowContext, CheckResult } from '../runtime';

export interface Check {
  name: string;
  run(ctx: WorkflowContext): Promise<CheckResult>;
}

export { type CheckResult, type WorkflowContext as CheckContext } from '../runtime';
