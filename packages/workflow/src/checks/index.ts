import type { WorkflowContext, CheckResult } from '../runtime';

export interface Check {
  name: string;
  run(ctx: WorkflowContext): Promise<CheckResult>;
}

export { type CheckResult, type WorkflowContext as CheckContext } from '../runtime';

export {
  type CheckProvider,
  type CheckProviderInput,
  type CheckRegistry,
  createCheckRegistry,
  resolveCheck,
  runCheck,
} from './check-registry';
