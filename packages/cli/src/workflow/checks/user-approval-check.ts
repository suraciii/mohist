import type { Check, CheckContext, CheckResult } from './index';
import type { ReactionConfig } from '../stage-context';
import { Stage } from '../../types';
import { isCurrentStageApproval } from '../issue-lifecycle';

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
    const issue = ctx.issue;

    if (!isCurrentStageApproval(issue, issue.stage, 'approved')) {
      if (isCurrentStageApproval(issue, issue.stage, 'rejected')) {
        return {
          name: this.name,
          status: 'fail',
          message: 'User rejected — escalating to prior stage',
        };
      }

      if (isCurrentStageApproval(issue, issue.stage, 'awaiting')) {
        return {
          name: this.name,
          status: 'pending',
          message: 'Waiting for user approval',
        };
      }

      return {
        name: this.name,
        status: 'pending',
        message: 'Waiting for user approval',
      };
    }

    return {
      name: this.name,
      status: 'pass',
      message: 'User approved',
    };
  }
}
