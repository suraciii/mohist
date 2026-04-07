import { z } from 'zod';
import { Tool, type ToolInstance } from '../agent-runtime/tool';
import { IssueService } from '../services/issue-service';
import { ExploreSessionRepo } from '../db/explore-session-repo';
import { eventBus } from '../services';

export interface CreateIssueToolContext {
  issueService: IssueService;
  exploreSessionRepo: ExploreSessionRepo;
  sessionId: string;
  projectId: string;
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
export function createCreateIssueTool(context: CreateIssueToolContext): ToolInstance<any> {
  return Tool.define('create_issue', {
    description:
      'Create a draft issue from the exploration results. Use this when requirements have converged and the user wants to crystallize the exploration into a structured issue. Provide a clear title and a structured body (background, expected behavior, constraints).',
    parameters: z.object({
      title: z.string().describe('Short, concise title for the issue'),
      body: z.string().describe(
        'Structured description including: background/context, expected behavior, constraints, non-goals'
      ),
      labels: z.array(z.string()).optional().describe('Labels to categorize the issue, e.g. ["refactor", "feature"]'),
    }),
    execute: async (params) => {
      const session = context.exploreSessionRepo.findById(context.sessionId);
      if (!session) {
        return `Error: explore session "${context.sessionId}" not found`;
      }

      const issue = context.issueService.create({
        projectId: context.projectId,
        title: params.title,
        body: params.body,
        labels: params.labels,
      });

      context.exploreSessionRepo.crystallize(context.sessionId, issue.id);

      eventBus.emit('explore_crystallized', {
        sessionId: context.sessionId,
        issueId: issue.id,
        projectId: context.projectId,
      });

      return `Issue #${issue.number} created and linked to explore session. Title: "${issue.title}"`;
    },
  });
}
