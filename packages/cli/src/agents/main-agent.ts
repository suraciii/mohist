import { resolveModel, type LlmConfig, SessionManager, type Session, ToolRegistry, runAgentLoop } from '../agent-runtime';
import type { AgentLoopResult } from '../agent-runtime';
import { IssueRepo } from '../db/issue-repo';
import { CommentRepo } from '../db/comment-repo';
import { QuestionRepo } from '../db/question-repo';
import type { Issue } from '../types';
import type { WorkflowLogRepo } from '../db/workflow-log-repo';
import type { AgentSessionMessageRepo } from '../db/agent-session-message-repo';
import type { CoderSessionRepo } from '../db/coder-session-repo';
import { createSpawnCoderTool } from '../tools/spawn-coder';
import { createReadWorkflowTool } from '../tools/read-workflow';
import { createAdvanceStageTool } from '../tools/advance-stage';
import { createAddCommentTool } from '../tools/add-comment';
import { createGetIssueTool } from '../tools/get-issue';
import { createAskUserTool } from '../tools/ask-user';
import { createArchiveChangeTool } from '../tools/archive-change';
import { createReadTasksTool } from '../tools/read-tasks';
import { createReadSpecTool } from '../tools/read-spec';
import { createStoreLearningTool, createLoadLearningsTool } from '../tools/session-memory';
import { createSelfReviewTool, createGenerateTasksTool } from '../tools/self-review';
import { createExecuteStageTool } from '../tools/execute-stage';
import { createSubmitApprovalTool } from '../tools/submit-approval';
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
  agentSessionMessageRepo?: AgentSessionMessageRepo;
  coderSessionRepo?: CoderSessionRepo;
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
   - The approval state has been persisted
   - Present the results summary to the user
   - Call submit_approval with your decision (approve/request_changes/abort)
4. If requiresApproval: false and execution was successful, automatically advance to next stage
5. Continue until the issue reaches "done".

## Available Tools
- **read_workflow**: Read the workflow configuration (stages, prompt templates, approval flags). Call this first.
- **spawn_coder**: Spawn an opencode acp oneshot session to execute a coding task. Provide \`taskTemplate\` (from the workflow stage prompt) and \`variables\` (issue info + previous stage outputs).
- **advance_stage**: Move the issue to the next stage. Only pass the target stage name.
- **add_comment**: Record progress notes, decisions, or summaries on the issue.
- **get_issue**: Check the current state of the issue at any time.
- **submit_approval**: Submit user approval decision after execute_stage returns requiresApproval: true. Options: approve (proceed to next stage), request_changes (retry current stage), abort (stop workflow).
- **ask_user**: Ask the user a question and wait for their reply (for clarifications, not stage approvals).
- **read_tasks**: Read the tasks.json file for the current OpenSpec Change.
- **read_spec**: Read a spec file from the current OpenSpec Change.
- **store_learning** / **load_learnings**: Store and retrieve session learnings for the current Change.
- **run_self_review**: Run self-review on the current OpenSpec specs.
- **generate_tasks**: Generate tasks.json from the current Change's specs.

## When to Use ask_user
- Requirements are ambiguous and you need clarification
- There are multiple valid approaches and you need the user to decide
- You discover a potential issue or trade-off that the user should be aware of

## When NOT to Use ask_user
- For stage approvals (use submit_approval instead)
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
   - If success: true and requiresApproval: true: Present results summary to user and call submit_approval
   - If success: false: Analyze error, add comment, and decide whether to retry or abort
4. For Plan and Review stages, always expect requiresApproval: true
5. For Build stage, if successful, automatically advance to Review

## User Approval Flow
When execute_stage returns requiresApproval: true:
1. The system has persisted the approval state
2. Present a clear summary of the results to the user
3. Call submit_approval with:
   - "approve" → use advance_stage to move to next stage
   - "request_changes" → the stage will be re-executed
   - "abort" → stop execution and add comment

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

${detection.mode === 'traditional' ? `A Change directory exists but tasks.json has not been generated yet. This means the plan stage is still in progress.

### Plan Stage Instructions
1. Use \`spawn_coder\` to explore the codebase and create the following files in the Change directory (${detection.changePath}):
   - \`proposal.md\`: Problem description and solution overview
   - \`design.md\`: Technical design document with design decisions
   - \`specs/{capability}/spec.md\`: Capability-based requirement specifications
2. After creating specs, use \`run_self_review\` to validate spec completeness (up to 3 iterations).
3. When self-review passes, use \`generate_tasks\` to generate tasks.json from the specs.
4. After tasks.json is generated, call \`advance_stage("review")\` to enter review stage (do NOT advance directly to "build").` : `tasks.json already exists. The plan stage has been completed.

### Next Steps
- If in plan stage: call \`advance_stage("review")\` to enter review stage.
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

  const model = await resolveModel(context.llmConfig);

  const toolRegistry = new ToolRegistry();
  toolRegistry.register(createSpawnCoderTool({
    worktreePath: context.worktreePath,
    issueId: context.issue.id,
    projectId: context.issue.projectId,
    workflowLogRepo: context.workflowLogRepo,
    eventBus: context.eventBus,
    toolRegistry,
    coderSessionRepo: context.coderSessionRepo,
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

  toolRegistry.register(createArchiveChangeTool({
    issue: context.issue,
    worktreePath: context.worktreePath,
  }));
  toolRegistry.register(createReadTasksTool({ projectPath: context.worktreePath }));
  toolRegistry.register(createReadSpecTool({ projectPath: context.worktreePath }));
  toolRegistry.register(createStoreLearningTool({ projectPath: context.worktreePath }));
  toolRegistry.register(createLoadLearningsTool({ projectPath: context.worktreePath }));
  toolRegistry.register(createSelfReviewTool({ projectPath: context.worktreePath }));
  toolRegistry.register(createGenerateTasksTool({ projectPath: context.worktreePath }));
  toolRegistry.register(createExecuteStageTool({
    workflowController,
    issue: context.issue,
    issueRepo: context.issueRepo,
  }));
  toolRegistry.register(createSubmitApprovalTool({
    issueRepo: context.issueRepo,
    issue: context.issue,
  }));

  const session = existingSession ?? sessionManager.create(Number(context.issue.id));
  session.metadata['openSpecDetection'] = openSpecDetection;
  const system = buildSystemPrompt(context.issue, openSpecDetection);

  const loopResult = await runAgentLoop(session, sessionManager, toolRegistry, model, {
    system,
    eventBus: context.eventBus,
    eventContext: { issueId: context.issue.id, projectId: context.issue.projectId },
    agentSessionMessageRepo: context.agentSessionMessageRepo,
  });

  return { loopResult, session };
}
