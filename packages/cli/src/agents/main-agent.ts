import { resolveModel, type LlmConfig, SessionManager, ToolRegistry, runAgentLoop } from '../agent-runtime';
import type { AgentLoopResult } from '../agent-runtime';
import { IssueRepo } from '../db/issue-repo';
import { CommentRepo } from '../db/comment-repo';
import type { Issue } from '../types';
import { createSpawnCoderTool } from '../tools/spawn-coder';
import { createReadWorkflowTool } from '../tools/read-workflow';
import { createAdvanceStageTool } from '../tools/advance-stage';
import { createAddCommentTool } from '../tools/add-comment';
import { createGetIssueTool } from '../tools/get-issue';
import type { EventBus } from '../services/event-bus';

export interface MainAgentContext {
  issueRepo: IssueRepo;
  commentRepo: CommentRepo;
  worktreePath: string;
  llmConfig?: LlmConfig;
  issue: Issue;
  eventBus?: EventBus;
}

function buildSystemPrompt(issue: Issue): string {
  return `You are the Mohist workflow orchestrator. You drive issues through configurable workflow stages by spawning opencode acp coding agents.

## Current Issue
- ID: ${issue.id}
- Number: #${issue.number}
- Title: ${issue.title}
- Stage: ${issue.stage}
${issue.body ? `- Description: ${issue.body}` : ''}

## How It Works
1. First, call \`read_workflow\` to read the workflow configuration. It returns the available stages, their prompt templates, and settings.
2. For each stage, call \`spawn_coder\` with the stage's prompt template and variables from the issue and previous stage results.
3. After spawn_coder completes, call \`advance_stage\` to move to the next stage.
4. If a stage has \`approval: true\`, do NOT advance to the next stage. Instead, add a comment summarizing the result and stop. The user will manually continue later.
5. Continue until the issue reaches "done".

## Available Tools
- **read_workflow**: Read the workflow configuration (stages, prompt templates, approval flags). Call this first.
- **spawn_coder**: Spawn an opencode acp oneshot session to execute a coding task. Provide \`taskTemplate\` (from the workflow stage prompt) and \`variables\` (issue info + previous stage outputs).
- **advance_stage**: Move the issue to the next stage. Only pass the target stage name.
- **add_comment**: Record progress notes, decisions, or summaries on the issue.
- **get_issue**: Check the current state of the issue at any time.

## Variables for spawn_coder
When calling spawn_coder, pass these variables in the \`variables\` object:
- \`issue\`: { number, title, body } — current issue info
- \`plan.output\`: the text result from the plan stage (after plan completes)
- \`build.output\`: the text result from the build stage (after build completes)
- \`check.output\`: the text result from the check stage (after check completes)

## Instructions
1. Call \`read_workflow\` to understand the workflow stages.
2. Find the current stage in the workflow and use its prompt template.
3. Call \`spawn_coder\` with the prompt template and variables.
4. After spawn_coder returns, call \`advance_stage\` with the next stage.
5. If the stage has \`approval: true\`, add a comment and stop instead of advancing.
6. Repeat until done.

## Error Handling
- If spawn_coder fails, analyze the error. You may retry with a more specific task, or advance to done with a comment explaining the failure.
- If check stage reveals issues, you may advance back to build to fix them.`;
}

export async function runMainAgent(
  context: MainAgentContext,
  sessionManager: SessionManager,
): Promise<AgentLoopResult> {
  const model = resolveModel(context.llmConfig);

  const toolRegistry = new ToolRegistry();
  toolRegistry.register(createSpawnCoderTool({ worktreePath: context.worktreePath }));
  toolRegistry.register(createReadWorkflowTool({ cwd: context.worktreePath }));
  toolRegistry.register(createAdvanceStageTool({ issue: context.issue, issueRepo: context.issueRepo, worktreePath: context.worktreePath, eventBus: context.eventBus }));
  toolRegistry.register(createAddCommentTool({ issue: context.issue, commentRepo: context.commentRepo, eventBus: context.eventBus }));
  toolRegistry.register(createGetIssueTool({ issue: context.issue, issueRepo: context.issueRepo }));

  const session = sessionManager.create(Number(context.issue.id));
  const system = buildSystemPrompt(context.issue);

  const result = await runAgentLoop(session, sessionManager, toolRegistry, model, {
    system,
  });

  sessionManager.close(session.id);

  return result;
}
