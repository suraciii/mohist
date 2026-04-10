import { z } from 'zod';
import { Tool, type ToolInstance } from '../agent-runtime/tool';
import type { IssueRepo } from '../db/issue-repo';
import type { Issue, ApprovalState } from '../types';

export interface SubmitApprovalContext {
  issueRepo: IssueRepo;
  issue: Issue;
}

export function createSubmitApprovalTool(context: SubmitApprovalContext): ToolInstance<any> {
  return Tool.define('submit_approval', {
    description:
      'Submit user approval decision for a workflow stage. Must be called after execute_stage returns requiresApproval: true. Valid options are: approve (proceed to next stage), request_changes (retry current stage), or abort (stop workflow).',
    parameters: z
      .object({
        decision: z
          .enum(['approve', 'request_changes', 'abort'])
          .describe('The approval decision'),
        comment: z
          .string()
          .optional()
          .describe('Optional comment explaining the decision'),
      })
      .strict(),
    execute: async (params) => {
      const issue = context.issueRepo.findById(context.issue.id);
      if (!issue) {
        return JSON.stringify({
          success: false,
          error: 'Issue not found',
        });
      }

      const approvalState = issue.approvalState;
      if (!approvalState || approvalState.status !== 'awaiting') {
        return JSON.stringify({
          success: false,
          error: 'No pending approval found for this issue. Execute the stage first.',
        });
      }

      const now = new Date().toISOString();
      const updatedApprovalState: ApprovalState = {
        ...approvalState,
        status: params.decision === 'approve' ? 'approved' : params.decision === 'request_changes' ? 'pending' : 'rejected',
        respondedAt: now,
      };

      context.issueRepo.setApprovalState(issue.id, updatedApprovalState);

      if (params.decision === 'approve') {
        return JSON.stringify({
          success: true,
          decision: 'approved',
          message: 'User approved. Use advance_stage to proceed to the next stage.',
          nextAction: 'advance_stage',
        });
      } else if (params.decision === 'request_changes') {
        return JSON.stringify({
          success: true,
          decision: 'changes_requested',
          message: 'User requested changes. The current stage will be re-executed.',
          nextAction: 'retry_stage',
        });
      } else {
        return JSON.stringify({
          success: true,
          decision: 'aborted',
          message: 'User aborted the workflow.',
          nextAction: 'abort',
        });
      }
    },
  });
}