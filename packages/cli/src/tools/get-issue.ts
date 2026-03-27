import { z } from 'zod';
import { Tool, type ToolInstance } from '../agent-runtime/tool';
import { IssueRepo } from '../db/issue-repo';

export interface GetIssueContext {
  issueRepo: IssueRepo;
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
export function createGetIssueTool(context: GetIssueContext): ToolInstance<any> {
  return Tool.define('get_issue', {
    description:
      'Get the current state of an issue including title, body, stage, number, and labels.',
    parameters: z.object({
      issue_id: z.string().describe('The internal ID of the issue to retrieve'),
    }),
    execute: async (params) => {
      const issue = context.issueRepo.findById(params.issue_id);
      if (!issue) {
        return `Error: issue not found with id "${params.issue_id}"`;
      }

      return [
        `Issue #${issue.number}`,
        `Title: ${issue.title}`,
        `Stage: ${issue.stage}`,
        `Status: ${issue.status}`,
        `Labels: ${issue.labels.length > 0 ? issue.labels.join(', ') : '(none)'}`,
        `Body: ${issue.body || '(no description)'}`,
        `Created: ${issue.createdAt}`,
        `Updated: ${issue.updatedAt}`,
      ].join('\n');
    },
  });
}
