## Context

Mohist spawns `opencode acp` subprocesses for each agent task. The prompt is assembled in TypeScript code and passed as a single user message via `connection.prompt({ prompt: [{ type: 'text', text: task }] })`. There is no system prompt injection — everything is user message content. The agent's only framing comes from the project's AGENTS.md (loaded by opencode itself) and the prompt text mohist provides.

Current state: three different prompt assembly styles exist across the codebase with no shared format or structure.

## Goals / Non-Goals

**Goals:**
- All agent prompts use a single XML-structured format (`<mohist-task>` envelope)
- Build stage prompts: proposal/design become file references, spec stays inline
- Every prompt gets `<project_context>` (tech stack, build/test commands) and `<rules>` (per-stage constraints)
- Every prompt gets `<role>` (what this session is doing) and `<contract>` (behavioral expectations)
- ~60% token reduction for build stage prompts on typical 5-11 task issues

**Non-Goals:**
- Schema-driven artifact graph (OpenSpec's `schema.yaml` approach) — over-engineering for mohist's needs
- Custom workflow schemas per project — mohist's stages are fixed (plan → build → check)
- Changing the opencode acp protocol or adding system prompt injection
- Rewriting the plan stage's round sequence (it already uses XML tags partially)

## Decisions

### D1: XML tags as prompt structure (not markdown, not JSON)

**Choice**: XML tags (`<task>`, `<spec>`, `<contract>`, etc.)

**Why**: OpenSpec has validated this format with real agents. XML tags give unambiguous semantic boundaries — the agent knows `<project_context>` is a constraint, `<template>` is output format, `<instruction>` is guidance. Markdown headers (`##`) don't provide this distinction. JSON would require the agent to parse structure before acting.

**Alternative considered**: Keep bracket-delimited `[Proposal]` format — rejected because it has no semantic annotation; `[Proposal]` could be a heading, a constraint, or a reference.

### D2: File references instead of inline content for proposal/design

**Choice**: Proposal and design listed in `<context-files>` with full paths and descriptions. Agent reads from disk when needed.

**Why**: These files exist in the worktree. The agent has `read_file` tool access. For a 5-task issue, the current approach sends ~8KB of proposal+design 5 times (~40KB total). With references, it sends ~200 bytes of file paths 5 times (~1KB total). The spec stays inline because it's task-specific and essential.

**Risk**: Agent might not read proposal/design when it needs architectural context.

**Mitigation**: `<contract>` section says "Read context-files if you need architectural guidance". For well-decomposed tasks with good specs, the agent won't need proposal/design at all. For edge cases, the instruction to read is explicit.

### D3: Learnings as file references, not inline

**Choice**: Previous task learnings go into `<context-files>` as `{changeDir}/session-memories/*.json`.

**Why**: Learnings grow linearly with completed tasks. For an 11-task issue, task T-011 would see 10 learning entries. Most are irrelevant (T-006 doesn't care what T-001 learned). As file references, the agent reads only when it encounters a similar problem.

### D4: Project agent config in workflow.yaml

**Choice**: Extend `workflow.yaml` with `agent.context` (project background) and `agent.rules` (per-stage rules).

**Why**: Currently every agent session must discover build commands, test framework, and code conventions from AGENTS.md/package.json. This wastes tool calls and tokens. A project-level config that gets injected into every prompt eliminates this discovery cost.

**Alternative considered**: Separate `agent-config.yaml` — rejected because workflow.yaml already exists and is the natural place for per-project agent configuration.

### D5: Single `formatAgentPrompt()` function as the only prompt assembly point

**Choice**: One function in a new `agent-prompt-schema.ts` that all prompt builders call.

**Why**: Eliminates the three-style fragmentation. If we want to change the prompt format later (add a new tag, change ordering), we change one function. The function signature is:

```typescript
interface AgentPromptParts {
  role: string;
  projectContext?: string;
  rules?: string[];
  contextFiles?: Array<{ path: string; desc: string }>;
  spec?: string;
  task: string;
  contract?: string;
  template?: string;
  instruction?: string;
}

function formatAgentPrompt(parts: AgentPromptParts): string
```

## Risks / Trade-offs

- [Prompt format change may affect agent quality] → Mitigate by testing on completed issues before rolling out
- [Agent might not read referenced files] → Mitigate with explicit `<contract>` instruction; verify with A/B test
- [Breaking change for existing tests] → Accept; update tests to match new format

## Migration Plan

1. Add `formatAgentPrompt()` — no existing code changes
2. Migrate `context-assembler.ts` to use it — build stage only
3. Add `loadAgentConfig()` to workflow-loader.ts
4. Migrate `artifact-prompt.ts` — plan + review stages
5. Migrate remaining prompt files — conflict-resolution, auto-fix, etc.
6. Remove old prompt assembly code

Each step is independently testable by running `mo issue start` on an existing issue.
