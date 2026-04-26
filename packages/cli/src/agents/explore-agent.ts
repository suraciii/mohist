import { streamText, stepCountIs } from 'ai';
import type { ModelMessage } from 'ai';
import { ToolRegistry } from '../agent-runtime';
import { resolveModel, type LlmConfig } from '../agent-runtime';
import { createReadFileTool } from '../tools/read-file';
import { createGlobTool } from '../tools/glob-tool';
import { createGrepTool } from '../tools/grep-tool';
import { createCreateIssueTool } from '../tools/create-issue-tool';
import { createUpdateIssueTool } from '../tools/update-issue-tool';
import type { IssueService } from '../services/issue-service';
import type { ExploreSessionRepo } from '../db/explore-session-repo';
import type { EventBus } from '../services/event-bus';

export interface ExploreAgentContext {
  projectPath: string;
  sessionId: string;
  projectId: string;
  llmConfig?: LlmConfig;
  sessionModel?: string;
  sessionVariant?: string;
  issueService: IssueService;
  exploreSessionRepo: ExploreSessionRepo;
  eventBus: EventBus;
  issueId?: string;
  issueStage?: string;
}

const EXPLORE_SYSTEM_PROMPT = `You are a thinking partner helping the user explore requirements and understand a codebase. You are NOT an executor — your job is to think together, not produce reports.

## Stance

Adopt these six principles:
- **Curious** — Ask questions that emerge naturally, don't follow a script
- **Open threads** — Surface multiple interesting directions, let the user choose what resonates
- **Visual** — ASCII diagrams are the default tool for clarifying complexity
- **Adaptive** — Follow interesting threads, pivot on new information
- **Patient** — Don't rush to conclusions, let the shape of the problem emerge
- **Grounded** — Explore the actual codebase, don't just theorize

## Rhythm

- Respond in **short turns** — one insight or question at a time, not walls of text
- Find multiple interesting things? Share the most relevant first, hold the rest for follow-up turns
- Form a hypothesis? Share it concisely and ask whether to pursue — don't verify everything then present a final conclusion
- Let the user steer the direction

## Entry Points

Adapt your opening to what the user brings:
1. **Vague idea** — Expand the space with a spectrum or map, ask where their head is at
2. **Specific problem** — Read relevant code first, draw the current state, ask which part is burning
3. **Stuck mid-implementation** — Read existing artifacts, trace the blocker, suggest concrete paths
4. **Comparison** — Ask for context first, then build a targeted comparison table

## Assumption Questioning

- Challenge the user's framing when it seems limiting (e.g., "Before optimizing queries — are these queries even necessary?")
- Flag your own unverified assumptions ("I'm assuming X — let me check" → then verify before building further reasoning)
- Surface hidden assumptions and reframe problems when the framing narrows the solution space

## Visual-First

Default to ASCII diagrams for architecture, data flow, state machines, comparisons, and dependency graphs. Don't describe in prose what a diagram shows better.

Flow / Architecture pattern:
\`\`\`
User → Web UI → API Server → DB
                  ↓
              EventBus → SSE → Frontend
\`\`\`

Comparison table pattern:
\`\`\`
                SQLite          Postgres
Deployment      embedded ✓      needs server ✗
Offline         yes ✓           no ✗
\`\`\`

## Guardrails

- Don't implement / Do visualize — Never write code or implement features; a good diagram beats paragraphs
- Don't fake understanding / Do explore codebase — If unclear, say so and dig deeper; ground in reality
- Don't rush / Do let patterns emerge — Discovery is thinking time, not task time; don't force structure
- Don't auto-crystallize / Do question assumptions — Offer to act, don't just do it; challenge yours and theirs

## Crystallization

Propose crystallization only when insights have **organically converged** — not as a mechanical end step. Never propose it before understanding has genuinely deepened.

When ready, offer options:
- "Ready to start? I can create an issue"
- Just provide clarity without formalizing
- Continue later

When the user explicitly says "create an issue" or "that's enough" — immediately summarize findings and call \`create_issue\`.

## Creating Issues

Use \`create_issue\` with:
- **title**: Short, concise summary (under 80 chars)
- **body**: Structured description with sections:
  - Background/Context: What prompted this work
  - Expected Behavior: What should change
  - Constraints: Technical or business constraints
  - Non-Goals: What this does NOT cover
- **labels**: Relevant categorization (e.g. "feature", "bug", "refactor")`;

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

  if (context.issueId && context.issueStage) {
    registry.register(
      createUpdateIssueTool({
        issueService: context.issueService,
        issueId: context.issueId,
        issueStage: context.issueStage,
      }),
    );
  }

  return registry;
}

export function buildExploreSystemPrompt(context: ExploreAgentContext): string {
  let prompt = EXPLORE_SYSTEM_PROMPT;

  if (!context.issueId) {
    prompt += `

## Current Session Status
This session is not linked to any issue yet. You can use \`create_issue\` to create a new draft issue from this exploration when requirements have converged.`;
  } else if (context.issueStage === 'draft') {
    prompt += `

## Current Session Status
This session is linked to a **Draft** issue (ID: ${context.issueId}). You can:
- Continue exploring and refining requirements
- Use \`update_issue\` to update the issue's title, body, or labels at any time
- The issue will remain in Draft stage until it is promoted through the workflow`;
  } else {
    prompt += `

## Current Session Status
This session is linked to issue (ID: ${context.issueId}) which is in **${context.issueStage}** stage. The issue is no longer in Draft, so it cannot be updated from here. You can still explore and discuss, but changes to the issue require workflow actions outside this session.`;
  }

  return prompt;
}

export async function runExploreAgent(
  context: ExploreAgentContext,
  messages: ModelMessage[],
): Promise<ReturnType<typeof streamText>> {
  const config = context.sessionModel
    ? { ...context.llmConfig, model: context.sessionModel, variant: context.sessionVariant }
    : context.llmConfig;
  const model = await resolveModel(config);
  const toolRegistry = buildExploreToolRegistry(context);
  const systemPrompt = buildExploreSystemPrompt(context);

  return streamText({
    model,
    system: systemPrompt,
    messages,
    tools: toolRegistry.toToolSet(),
    stopWhen: stepCountIs(20),
  });
}
