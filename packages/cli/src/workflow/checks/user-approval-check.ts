import type { Check, CheckContext, CheckResult } from './index';
import type { ReactionConfig } from '../stage-context';
import { Stage } from '../../types';

export class UserApprovalCheck implements Check {
  public readonly name = 'user-approval';
  public readonly reaction: ReactionConfig;
  private escalateTarget: Stage;

  constructor(escalateTarget: Stage) {
    this.escalateTarget = escalateTarget;
    this.reaction = {
      type: 'ask-user',
      fallbackReaction: {
        type: 'escalate',
        escalateTarget: this.escalateTarget,
      },
    };
  }

  async run(ctx: CheckContext): Promise<CheckResult> {
    if (ctx.issue.approvalState?.status === 'approved') {
      return {
        name: this.name,
        status: 'pass',
        message: 'User approved',
      };
    }

    return {
      name: this.name,
      status: 'pending',
      message: 'Waiting for user approval',
    };
  }
}
