import { z } from 'zod';
import { Tool, type ToolInstance } from '../agent-runtime/tool';
import { IssueRepo } from '../db/issue-repo';
import type { Issue } from '../types';
import { Stage, isValidTransition } from '../types';
import { loadWorkflow } from '../workflow/workflow-loader';
import type { EventBus } from '../services/event-bus';

const VALID_STAGES = new Set(Object.values(Stage));

export interface AdvanceStageContext {
  issue: Issue;
  issueRepo: IssueRepo;
  worktreePath?: string;
  eventBus?: EventBus;
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
export function createAdvanceStageTool(context: AdvanceStageContext): ToolInstance<any> {
  return Tool.define('advance_stage', {
    description:
      'Advance the current issue to the next workflow stage. Allowed transitions: explore → plan, draft → plan, plan → build, build → review, review → done/build, check → done/plan.',
    parameters: z.object({
      stage: z
        .string()
        .describe(
          'The target stage to advance to. One of: plan, build, check, done (depending on current stage)'
        ),
    }),
    execute: async (params) => {
      const stage = params.stage as Stage;
      const issue = context.issueRepo.findById(context.issue.id);
      if (!issue) {
        return `Error: issue "${context.issue.id}" not found`;
      }

      if (!VALID_STAGES.has(stage)) {
        return `Error: invalid stage "${stage}". Valid stages: ${Array.from(VALID_STAGES).join(', ')}`;
      }

      if (!isValidTransition(issue.stage, stage)) {
        return `Error: cannot advance from "${issue.stage}" to "${stage}". Invalid stage transition.`;
      }

      const fromStage = issue.stage;
      const updated = context.issueRepo.updateStage(issue.id, stage);
      if (!updated) {
        return `Error: failed to update issue "${issue.id}" to stage "${stage}"`;
      }

      if (context.eventBus) {
        context.eventBus.emit('stage_changed', {
          issueId: issue.id,
          projectId: issue.projectId,
          from: fromStage,
          to: stage,
        });

        if (context.worktreePath) {
          const workflow = loadWorkflow(context.worktreePath);
          if (typeof workflow !== 'string') {
            const targetStageConfig = workflow.stages.find((s) => s.stage === stage);
            const approval = targetStageConfig?.approval ?? true;
            if (approval) {
              context.eventBus.emit('approval_requested', {
                issueId: issue.id,
                projectId: issue.projectId,
                stage,
              });
            }
          }
        }
      }

      return `Issue #${issue.number} advanced from "${fromStage}" to "${stage}"`;
    },
  });
}
