import { z } from 'zod';
import { Tool, type ToolInstance } from '../agent-runtime/tool';
import { CommentRepo } from '../db/comment-repo';
import type { Issue } from '../types';

export interface AddCommentContext {
  issue: Issue;
  commentRepo: CommentRepo;
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
export function createAddCommentTool(context: AddCommentContext): ToolInstance<any> {
  return Tool.define('add_comment', {
    description:
      'Add a comment to the current issue. Comments are used to record progress notes, design decisions, or observations during workflow stages.',
    parameters: z.object({
      body: z.string().describe('The comment text to add'),
    }),
    execute: async (params) => {
      const comment = context.commentRepo.create({
        issueId: context.issue.id,
        body: params.body,
      });

      return `Comment added (id: ${comment.id}) at ${comment.createdAt}`;
    },
  });
}
