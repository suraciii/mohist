import type { CheckContext, CheckResult } from '../runtime';

export interface Check {
  name: string;
  run(ctx: CheckContext): Promise<CheckResult>;
}

export { type CheckResult, type CheckContext } from '../runtime';

export {
  type CheckProvider,
  type CheckProviderInput,
  type CheckRegistry,
  createCheckRegistry,
  resolveCheck,
  runCheck,
} from './check-registry';
