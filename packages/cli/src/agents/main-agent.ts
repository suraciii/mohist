import { resolveModel, type LlmConfig, SessionManager, type Session, ToolRegistry, runAgentLoop } from '../agent-runtime';
import type { AgentLoopResult } from '../agent-runtime';
import { IssueRepo } from '../db/issue-repo';
import { CommentRepo } from '../db/comment-repo';
import { QuestionRepo } from '../db/question-repo';
import type { Issue } from '../types';
import type { WorkflowLogRepo } from '../db/workflow-log-repo';
import { createSpawnCoderTool } from '../tools/spawn-coder';
import { createReadWorkflowTool } from '../tools/read-workflow';
import { createAdvanceStageTool } from '../tools/advance-stage';
import { createAddCommentTool } from '../tools/add-comment';
import { createGetIssueTool } from '../tools/get-issue';
import { createAskUserTool } from '../tools/ask-user';
import { createRunRalphLoopTool } from '../tools/run-ralph-loop';
import { createArchiveChangeTool } from '../tools/archive-change';
import { createReadPrdTool } from '../tools/read-prd';
import { createReadSpecTool } from '../tools/read-spec';
import { createStoreLearningTool, createLoadLearningsTool } from '../tools/session-memory';
import { createUpdateTaskStatusTool, createGetTaskStatusTool } from '../tools/task-status';
import { createSelfReviewTool, createGeneratePrdTool } from '../tools/self-review';
import type { EventBus } from '../services/event-bus';
import { detectOpenSpecForIssue, type OpenSpecDetection } from '../workflow/workflow-loader';

export interface MainAgentContext {
  issueRepo: IssueRepo;
  commentRepo: CommentRepo;
  questionRepo?: QuestionRepo;
  worktreePath: string;
  llmConfig?: LlmConfig;
  issue: Issue;
  eventBus?: EventBus;
  workflowLogRepo?: WorkflowLogRepo;
  onWaitingChange?: (issueId: string, questionId: string | null, question?: string) => void;
}

function buildSystemPrompt(issue: Issue, detection: OpenSpecDetection): string {
  const basePrompt = `You are the Mohist workflow orchestrator. You drive issues through configurable workflow stages by spawning opencode acp coding agents.

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
- **run_ralph_loop**: Run Ralph task loop for OpenSpec workflow. Use in build stage when OpenSpec Change is detected. Detects Change directory and executes tasks from prd.json sequentially.
- **advance_stage**: Move the issue to the next stage. Only pass the target stage name.
- **add_comment**: Record progress notes, decisions, or summaries on the issue.
- **get_issue**: Check the current state of the issue at any time.
- **ask_user**: Ask the user a question and wait for their reply. The tool blocks until the user responds or a 24h timeout expires.
- **read_prd**: Read the prd.json file for the current OpenSpec Change.
- **read_spec**: Read a spec file from the current OpenSpec Change.
- **store_learning** / **load_learnings**: Store and retrieve session learnings for the current Change.
- **update_task_status** / **get_task_status**: Update and query task status in prd.json.
- **run_self_review**: Run self-review on the current OpenSpec specs.
- **generate_prd**: Generate prd.json from the current Change's specs.

## Ralph Loop (OpenSpec Workflow)
When you call \`read_workflow\`, it will automatically detect whether an OpenSpec Change exists for this issue and report the execution mode.
- If execution mode is "Ralph-style task loop": use \`run_ralph_loop\` in the build stage instead of \`spawn_coder\`
- If execution mode is "Traditional": use \`spawn_coder\` for all stages as usual
- Detection is based on the presence of \`.mohist-specs/changes/{issue-number}-{slug}/prd.json\`
- A Change directory without prd.json means plan stage is still in progress

## When to Use ask_user
- Requirements are ambiguous and you need clarification
- There are multiple valid approaches and you need the user to decide
- You discover a potential issue or trade-off that the user should be aware of

## When NOT to Use ask_user
- You can solve the problem with the available tools (spawn_coder, etc.)
- There is a clear best practice or convention to follow
- The question is purely technical and you have enough information to decide

## ask_user Guidelines
- Ask one question at a time
- Make questions specific and actionable (avoid open-ended questions)
- Provide context and options when asking, e.g. "Should I use approach A (benefit X) or approach B (benefit Y)?"

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

  if (!detection.detected) {
    return basePrompt;
  }

  const openspecSection = `

## OpenSpec Plan Stage (Active)
An OpenSpec Change has been detected for this issue:
- Change directory: ${detection.changePath}
- Mode: ${detection.mode}

${detection.mode === 'traditional' ? `A Change directory exists but prd.json has not been generated yet. This means the plan stage is still in progress.

### Plan Stage Instructions
1. Use \`spawn_coder\` to explore the codebase and create the following files in the Change directory (${detection.changePath}):
   - \`proposal.md\`: Problem description and solution overview
   - \`design.md\`: Technical design document with design decisions
   - \`specs/{capability}/spec.md\`: Capability-based requirement specifications
2. After creating specs, use \`run_self_review\` to validate spec completeness (up to 3 iterations).
3. When self-review passes, use \`generate_prd\` to generate prd.json from the specs.
4. After prd.json is generated, call \`advance_stage("review")\` to enter review stage (do NOT advance directly to "build").` : `prd.json already exists. The plan stage has been completed.

### Next Steps
- If in plan stage: call \`advance_stage("review")\` to enter review stage.
- If in build stage: use \`run_ralph_loop\` to execute tasks from prd.json.
- If in check stage: use \`spawn_coder\` to run tests and lint.`}`;

  return basePrompt + openspecSection;
}

export interface MainAgentResult {
  loopResult: AgentLoopResult;
  session: Session;
}

export async function runMainAgent(
  context: MainAgentContext,
  sessionManager: SessionManager,
  existingSession?: Session,
): Promise<MainAgentResult> {
  const openSpecDetection = await Promise.resolve(
    detectOpenSpecForIssue(context.worktreePath, context.issue.number),
  );

  const model = resolveModel(context.llmConfig);

  const toolRegistry = new ToolRegistry();
  toolRegistry.register(createSpawnCoderTool({
    worktreePath: context.worktreePath,
    issueId: context.issue.id,
    projectId: context.issue.projectId,
    workflowLogRepo: context.workflowLogRepo,
    eventBus: context.eventBus,
  }));
  toolRegistry.register(createReadWorkflowTool({ cwd: context.worktreePath, issueNumber: context.issue.number }));
  toolRegistry.register(createAdvanceStageTool({ issue: context.issue, issueRepo: context.issueRepo, worktreePath: context.worktreePath, eventBus: context.eventBus }));
  toolRegistry.register(createAddCommentTool({ issue: context.issue, commentRepo: context.commentRepo, eventBus: context.eventBus }));
  toolRegistry.register(createGetIssueTool({ issue: context.issue, issueRepo: context.issueRepo }));
  if (context.questionRepo && context.eventBus) {
    toolRegistry.register(createAskUserTool({
      questionRepo: context.questionRepo,
      issueRepo: context.issueRepo,
      eventBus: context.eventBus,
      issueId: context.issue.id,
      projectId: context.issue.projectId,
      onWaitingChange: context.onWaitingChange,
    }));
  }
  toolRegistry.register(createRunRalphLoopTool({
    worktreePath: context.worktreePath,
    issueId: context.issue.id,
    projectId: context.issue.projectId,
  }));
  toolRegistry.register(createArchiveChangeTool({
    issue: context.issue,
    worktreePath: context.worktreePath,
  }));
  toolRegistry.register(createReadPrdTool({ projectPath: context.worktreePath }));
  toolRegistry.register(createReadSpecTool({ projectPath: context.worktreePath }));
  toolRegistry.register(createStoreLearningTool({ projectPath: context.worktreePath }));
  toolRegistry.register(createLoadLearningsTool({ projectPath: context.worktreePath }));
  toolRegistry.register(createUpdateTaskStatusTool({ projectPath: context.worktreePath }));
  toolRegistry.register(createGetTaskStatusTool({ projectPath: context.worktreePath }));
  toolRegistry.register(createSelfReviewTool({ projectPath: context.worktreePath }));
  toolRegistry.register(createGeneratePrdTool({ projectPath: context.worktreePath }));

  const session = existingSession ?? sessionManager.create(Number(context.issue.id));
  session.metadata['openSpecDetection'] = openSpecDetection;
  const system = buildSystemPrompt(context.issue, openSpecDetection);

  const loopResult = await runAgentLoop(session, sessionManager, toolRegistry, model, {
    system,
    eventBus: context.eventBus,
    eventContext: { issueId: context.issue.id, projectId: context.issue.projectId },
  });

  return { loopResult, session };
}
