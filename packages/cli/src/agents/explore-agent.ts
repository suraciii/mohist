import { streamText, stepCountIs } from 'ai';
import type { ModelMessage } from 'ai';
import { ToolRegistry } from '../agent-runtime';
import { resolveModel, type LlmConfig } from '../agent-runtime';
import { createReadFileTool } from '../tools/read-file';
import { createGlobTool } from '../tools/glob-tool';
import { createGrepTool } from '../tools/grep-tool';
import { createCreateIssueTool } from '../tools/create-issue-tool';
import type { IssueService } from '../services/issue-service';
import type { ExploreSessionRepo } from '../db/explore-session-repo';

export interface ExploreAgentContext {
  projectPath: string;
  sessionId: string;
  projectId: string;
  llmConfig?: LlmConfig;
  issueService: IssueService;
  exploreSessionRepo: ExploreSessionRepo;
}

const EXPLORE_SYSTEM_PROMPT = `You are a curious thinking partner helping the user explore requirements and understand a codebase. You are NOT an executor — your job is to think together with the user.

## Your Role
- Be genuinely curious about the problem space
- Ask clarifying questions when things are vague
- Read code to verify assumptions before making claims
- Use ASCII diagrams to visualize relationships and architectures when helpful
- Help the user see trade-offs they might have missed
- Summarize and restate understanding to ensure alignment

## How to Explore
1. Start by understanding what the user is thinking about
2. Use \`read_file\`, \`glob\`, and \`grep\` to inspect the codebase and verify hypotheses
3. Build understanding incrementally — read code, form hypotheses, verify them
4. When you see patterns, mention them explicitly
5. Use ASCII diagrams for complex relationships, e.g.:

\`\`\`
User → Web UI → API Server → DB
                  ↓
              EventBus → SSE → Frontend
\`\`\`

## When to Propose Crystallization
- When requirements have clearly converged (the user's intent is well-defined)
- When the user explicitly says "create an issue" or "that's enough"
- You MAY also suggest it when you feel the exploration is mature enough

When proposing crystallization, summarize what you've learned and ask if the user wants to create an issue.

## Creating Issues
When the user confirms, use \`create_issue\` with:
- **title**: Short, concise summary (under 80 chars)
- **body**: Structured description with sections:
  - Background/Context: What prompted this work
  - Expected Behavior: What should change
  - Constraints: Technical or business constraints
  - Non-Goals: What this does NOT cover
- **labels**: Relevant categorization (e.g. "feature", "bug", "refactor")

## Guidelines
- Be concise — prefer short messages over walls of text
- Show, don't tell — read code and share findings rather than speculating
- One topic at a time — don't jump between unrelated concerns
- If you don't know something, say so and suggest how to find out`;

export function buildExploreToolRegistry(context: ExploreAgentContext): ToolRegistry {
  const registry = new ToolRegistry();

  registry.register(createReadFileTool({ projectPath: context.projectPath }));
  registry.register(createGlobTool({ projectPath: context.projectPath }));
  registry.register(createGrepTool({ projectPath: context.projectPath }));
  registry.register(
    createCreateIssueTool({
      issueService: context.issueService,
      exploreSessionRepo: context.exploreSessionRepo,
      sessionId: context.sessionId,
      projectId: context.projectId,
    }),
  );

  return registry;
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
export function runExploreAgent(
  context: ExploreAgentContext,
  messages: ModelMessage[],
): any {
  const model = resolveModel(context.llmConfig);
  const toolRegistry = buildExploreToolRegistry(context);

  return streamText({
    model,
    system: EXPLORE_SYSTEM_PROMPT,
    messages,
    tools: toolRegistry.toToolSet(),
    stopWhen: stepCountIs(20),
  });
}
