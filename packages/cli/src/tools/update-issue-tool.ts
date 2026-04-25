import { z } from 'zod';
import { Tool, type ToolInstance } from '../agent-runtime/tool';
import type { IssueService } from '../services/issue-service';
import { Stage } from '../types';

export interface UpdateIssueToolContext {
  issueService: IssueService;
  issueId: string;
  issueStage: string;
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
export function createUpdateIssueTool(context: UpdateIssueToolContext): ToolInstance<any> {
  return Tool.define('update_issue', {
    description:
      'Update the draft issue linked to this explore session. Only available when the linked issue is still in Draft stage. Provide the fields you want to change.',
    parameters: z.object({
      title: z.string().optional().describe('Updated title for the issue'),
      body: z.string().optional().describe('Updated structured description'),
      labels: z.array(z.string()).optional().describe('Updated labels (replaces all existing labels)'),
    }),
    execute: async (params) => {
      if (context.issueStage !== Stage.Draft) {
        return `Error: cannot update issue — it is no longer in Draft stage (current: ${context.issueStage}). Only Draft issues can be updated from explore.`;
      }

      const updates: Partial<{ title: string; body: string; labels: string[] }> = {};
      if (params.title !== undefined) updates.title = params.title;
      if (params.body !== undefined) updates.body = params.body;
      if (params.labels !== undefined) updates.labels = params.labels;

      if (Object.keys(updates).length === 0) {
        return `Error: no fields provided to update. Specify at least one of: title, body, labels.`;
      }

      const issue = context.issueService.update(context.issueId, updates);
      if (!issue) {
        return `Error: issue with id "${context.issueId}" not found.`;
      }

      return `Issue #${issue.number} updated successfully. Title: "${issue.title}"`;
    },
  });
}
