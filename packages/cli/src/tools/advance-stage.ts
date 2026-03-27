import { z } from 'zod';
import { Tool, type ToolInstance } from '../agent-runtime/tool';
import { IssueRepo } from '../db/issue-repo';
import { Stage } from '../types';

const VALID_STAGES = new Set(Object.values(Stage));

export interface AdvanceStageContext {
  issueRepo: IssueRepo;
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
export function createAdvanceStageTool(context: AdvanceStageContext): ToolInstance<any> {
  return Tool.define('advance_stage', {
    description:
      'Advance the current issue to a new workflow stage. Valid stages: draft, designing, waiting-design-review, implementing, waiting-review, done.',
    parameters: z.object({
      issue_id: z.string().describe('The internal ID of the issue to update'),
      stage: z
        .string()
        .describe(
          'The target stage to advance to. One of: draft, designing, waiting-design-review, implementing, waiting-review, done'
        ),
    }),
    execute: async (params) => {
      const stage = params.stage as Stage;

      if (!VALID_STAGES.has(stage)) {
        return `Error: invalid stage "${stage}". Valid stages: ${Array.from(VALID_STAGES).join(', ')}`;
      }

      const issue = context.issueRepo.findById(params.issue_id);
      if (!issue) {
        return `Error: issue not found with id "${params.issue_id}"`;
      }

      const updated = context.issueRepo.updateStage(params.issue_id, stage);
      if (!updated) {
        return `Error: failed to update issue "${params.issue_id}" to stage "${stage}"`;
      }

      return `Issue #${issue.number} advanced from "${issue.stage}" to "${stage}"`;
    },
  });
}
