import { z } from 'zod';
import { Tool, type ToolInstance } from '../agent-runtime/tool';
import type { WorkflowController } from '../workflow/workflow-controller';
import type { Issue, Stage } from '../types';

export interface ExecuteStageContext {
  workflowController: WorkflowController;
  issue: Issue;
}

export function createExecuteStageTool(context: ExecuteStageContext): ToolInstance<any> {
  return Tool.define('execute_stage', {
    description:
      'Execute the current workflow stage (Plan, Build, or Review). This will run the appropriate agent and return the result. If requiresApproval is true, you must ask the user for approval before proceeding.',
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
          return JSON.stringify({
            success: result.success,
            requiresApproval: true,
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
