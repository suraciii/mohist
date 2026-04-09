import { z } from 'zod';
import { Tool, type ToolInstance } from '../agent-runtime/tool';
import type { WorkflowController } from '../workflow/workflow-controller';
import type { Issue, Stage, ApprovalState } from '../types';

export interface ExecuteStageContext {
  workflowController: WorkflowController;
  issue: Issue;
  issueRepo?: any;
}

export function createExecuteStageTool(context: ExecuteStageContext): ToolInstance<any> {
  return Tool.define('execute_stage', {
    description:
      'Execute the current workflow stage (Plan, Build, or Review). This will run the appropriate agent and return the result. If requiresApproval is true, you must submit approval using submit_approval tool before proceeding.',
    parameters: z
      .object({
        stage: z
          .enum(['explore', 'plan', 'build', 'review', 'done'])
          .describe('The workflow stage to execute'),
      })
      .strict(),
    execute: async (params) => {
      try {
        const stage = params.stage as Stage;
        const result = await context.workflowController.executeStage(context.issue, stage);

        if (result.requiresApproval) {
          if (context.issueRepo) {
            const approvalState: ApprovalState = {
              stage: params.stage as Stage,
              status: 'awaiting',
              output: result.output,
              requestedAt: new Date().toISOString(),
            };
            context.issueRepo.setApprovalState(context.issue.id, approvalState);
          }

          return JSON.stringify({
            success: result.success,
            requiresApproval: true,
            status: 'awaiting_approval',
            stage: params.stage,
            output: result.output,
            message: result.message,
          }, null, 2);
        }

        return JSON.stringify({
          success: result.success,
          requiresApproval: false,
          stage: params.stage,
          output: result.output,
          message: result.message,
        }, null, 2);
      } catch (error) {
        return `Error executing stage: ${error instanceof Error ? error.message : 'Unknown error'}`;
      }
    },
  });
}
