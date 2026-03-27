import { resolveModel, type LlmConfig, SessionManager, ToolRegistry, runAgentLoop } from '../agent-runtime';
import type { AgentLoopResult } from '../agent-runtime';
import { IssueRepo } from '../db/issue-repo';
import { CommentRepo } from '../db/comment-repo';
import { createSpawnAgentTool } from '../tools/spawn-agent';
import { createAdvanceStageTool } from '../tools/advance-stage';
import { createAddCommentTool } from '../tools/add-comment';
import { createGetIssueTool } from '../tools/get-issue';

export interface MainAgentContext {
  issueRepo: IssueRepo;
  commentRepo: CommentRepo;
  worktreePath: string;
  llmConfig?: LlmConfig;
}

interface IssueInfo {
  id: string;
  number: number;
  title: string;
  body?: string;
}

function buildSystemPrompt(issue: IssueInfo): string {
  return `You are the Mohist workflow orchestrator. You drive issues from creation to completion using a two-stage workflow.

## Current Issue
- Number: #${issue.number}
- Title: ${issue.title}
${issue.body ? `- Description: ${issue.body}` : ''}

## Workflow Stages
1. **design** — Analyze the issue and create a design document. Use \`spawn_agent\` with a code agent to explore the codebase and produce a design.
2. **implement** — Implement the solution based on the design. Use \`spawn_agent\` with a code agent to write the code.
3. **done** — The issue is complete. Call \`advance_stage\` with stage "done" when all work is finished.

## Available Tools
- **spawn_agent**: Spawn an opencode subprocess to execute tasks in the issue worktree. Use this for all code work (design, implementation, testing).
- **advance_stage**: Move the issue to the next workflow stage. Always call this after completing a stage.
- **add_comment**: Record observations, decisions, or progress notes on the issue.
- **get_issue**: Check the current state of the issue at any time.

## Instructions
1. Start by reading the issue details with \`get_issue\`.
2. For the **design** stage: call \`spawn_agent\` to create a design document. Then call \`advance_stage\` with stage "implementing".
3. For the **implement** stage: call \`spawn_agent\` to implement the solution. Then call \`advance_stage\` with stage "done".
4. After advancing to "done", add a final comment summarizing what was accomplished.

## Error Handling
- If \`spawn_agent\` fails, analyze the error and retry with a more specific task description.
- If an issue persists after 2 retries, add a comment explaining the problem and advance to "done" anyway.
- Keep task descriptions clear and specific for the code agent.`;
}

export async function runMainAgent(
  issue: IssueInfo,
  context: MainAgentContext,
  sessionManager: SessionManager,
): Promise<AgentLoopResult> {
  const model = resolveModel(context.llmConfig);

  const toolRegistry = new ToolRegistry();
  toolRegistry.register(createSpawnAgentTool(context.worktreePath));
  toolRegistry.register(createAdvanceStageTool({ issueRepo: context.issueRepo }));
  toolRegistry.register(createAddCommentTool({ commentRepo: context.commentRepo }));
  toolRegistry.register(createGetIssueTool({ issueRepo: context.issueRepo }));

  const session = sessionManager.create(Number(issue.id));
  const system = buildSystemPrompt(issue);

  const result = await runAgentLoop(session, sessionManager, toolRegistry, model, {
    system,
  });

  sessionManager.close(session.id);

  return result;
}
