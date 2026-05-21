import type { Check, CheckContext, CheckResult } from '@mohist/workflow/checks';
import { isCurrentStageApproval } from '../../issue-lifecycle';
import type { Issue } from '../../../types';

export class UserApprovalCheck implements Check {
  public readonly name = 'user-approval';

  constructor(_escalateTarget?: unknown) {}

  async run(ctx: CheckContext): Promise<CheckResult> {
    const issue = ctx.issue as Issue;

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
