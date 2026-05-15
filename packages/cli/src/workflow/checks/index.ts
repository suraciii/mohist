import type { CheckContext, CheckResult } from '../stage-context';

export interface Check {
  name: string;
  run(ctx: CheckContext): Promise<CheckResult>;
}

export { type CheckResult, type CheckContext } from '../stage-context';

export {
  type CheckFactory,
  type CheckRegistry,
  createCheckRegistry,
  resolveCheck,
  runCheck,
} from './check-registry';
