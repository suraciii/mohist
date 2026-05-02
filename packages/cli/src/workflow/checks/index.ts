import type { CheckContext, CheckResult, ReactionConfig } from '../stage-context';

export interface Check {
  name: string;
  reaction: ReactionConfig;
  run(ctx: CheckContext): Promise<CheckResult>;
  fix?(ctx: CheckContext): Promise<void>;
}

export { type CheckResult, type CheckContext, type ReactionConfig } from '../stage-context';
