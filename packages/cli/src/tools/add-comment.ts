import { z } from 'zod';
import { Tool, type ToolInstance } from '../agent-runtime/tool';
import { CommentRepo } from '../db/comment-repo';

export interface AddCommentContext {
  commentRepo: CommentRepo;
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
export function createAddCommentTool(context: AddCommentContext): ToolInstance<any> {
  return Tool.define('add_comment', {
    description:
      'Add a comment to the current issue. Comments are used to record progress notes, design decisions, or observations during workflow stages.',
    parameters: z.object({
      issue_id: z.string().describe('The internal ID of the issue to comment on'),
      body: z.string().describe('The comment text to add'),
    }),
    execute: async (params) => {
      const comment = context.commentRepo.create({
        issueId: params.issue_id,
        body: params.body,
      });

      return `Comment added (id: ${comment.id}) at ${comment.createdAt}`;
    },
  });
}
