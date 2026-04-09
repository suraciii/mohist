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
import { createExecuteStageTool } from '../tools/execute-stage';
import type { EventBus } from '../services/event-bus';
import { detectOpenSpecForIssue, type OpenSpecDetection } from '../workflow/workflow-loader';
import { WorkflowController, createWorkflowController } from '../workflow/workflow-controller';
import { createPlannerAgent } from './planner-agent';
import { createReviewerAgent } from './reviewer-agent';
import { ChangeArtifactsManager } from '../artifacts/change-artifacts-manager';

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
  workflowController?: WorkflowController;
}

function buildSystemPrompt(issue: Issue, detection: OpenSpecDetection): string {
  const basePrompt = `You are the Mohist workflow orchestrator. You drive issues through configurable workflow stages by spawning opencode acp coding agents.

## Current Issue
- ID: ${issue.id}
- Number: #${issue.number}
- Title: ${issue.title}
- Stage: ${issue.stage}
${issue.body ? `- Description: ${issue.body}` : ''}

## How It Works (New Workflow)
1. Check the current issue stage.
2. Call execute_stage with the current stage to execute the appropriate agent:
   - Plan stage: Planner Agent generates design artifacts
   - Build stage: Sequential task execution using Coder Agent
   - Review stage: Reviewer Agent reviews code quality
3. If execute_stage returns requiresApproval: true:
   - Present the results to the user
   - Use ask_user to get approval decision (approve/request changes/abort)
   - If approved, call advance_stage to move to next stage
   - If changes requested, the current stage will be re-executed
   - If aborted, stop the workflow
4. If requiresApproval: false and execution was successful, automatically advance to next stage
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

## Instructions (New Workflow)
1. Check the current issue stage using get_issue.
2. Call execute_stage with the current stage name.
3. Parse the result:
   - If success: true and requiresApproval: false: Execution completed, call advance_stage to next stage
   - If success: true and requiresApproval: true: Present results to user and call ask_user for approval
   - If success: false: Analyze error, add comment, and decide whether to retry or abort
4. For Plan and Review stages, always expect requiresApproval: true and get user confirmation
5. For Build stage, if successful, automatically advance to Review

## User Approval Flow
When requiresApproval: true:
1. Present a clear summary of the results
2. Call ask_user with specific options:
   - "Approve and continue" → call advance_stage to next stage
   - "Request changes" → the stage will be re-executed (for Plan/Review)
   - "Abort workflow" → stop execution and add comment

## Error Handling
- If execute_stage fails, analyze the error message
- You may retry the same stage with modifications
- For persistent failures, ask the user for guidance using \`ask_user\`
- Always add comments to document decisions and issues`;

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

  // Initialize WorkflowController if not provided
  const workflowController = context.workflowController ?? createWorkflowController({
    plannerAgent: createPlannerAgent({
      llmConfig: context.llmConfig,
      artifactManager: new ChangeArtifactsManager(context.worktreePath),
    }),
    reviewerAgent: createReviewerAgent({
      llmConfig: context.llmConfig,
    }),
    artifactManager: new ChangeArtifactsManager(context.worktreePath),
    worktreePath: context.worktreePath,
  });

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
  toolRegistry.register(createExecuteStageTool({
    workflowController,
    issue: context.issue,
  }));

  const session = existingSession ?? sessionManager.create(Number(context.issue.id));
  session.metadata['openSpecDetection'] = openSpecDetection;
  const system = buildSystemPrompt(context.issue, openSpecDetection);

  const loopResult = await runAgentLoop(session, sessionManager, toolRegistry, model, {
    system,
  });

  return { loopResult, session };
}
