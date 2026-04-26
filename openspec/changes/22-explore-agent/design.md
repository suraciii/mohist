## Context

The Explore Agent has two distinct prompt surfaces:

1. **`EXPLORE_SYSTEM_PROMPT`** in `explore-agent.ts` — the system prompt for the **interactive** explore agent (user chat, streaming). This is the primary target: it currently produces report-style output instead of acting as a thinking partner.

2. **`prompts/explore.md`** — loaded by `buildExplorePrompt()` in `artifact-prompt.ts`, used for the **pipeline's explore stage** (automated proposal generation when an issue enters the explore workflow stage). This is a task instruction for generating proposals, not a conversation stance.

The reference model is `.opencode/skills/openspec-explore/SKILL.md` — a comprehensive thinking-partner stance with entry-point differentiation, guardrails, visual-first habits, and natural crystallization timing.

## Goals / Non-Goals

**Goals:**
- Rewrite `EXPLORE_SYSTEM_PROMPT` to produce thinking-partner behavior: short turns, open threads, visual-first, assumption questioning, natural crystallization
- Keep the prompt under ~8000 characters (~2000 tokens)
- Update `explore.md` to incorporate the same exploration principles (curiosity, codebase grounding, visual thinking) into its proposal-generation instructions

**Non-Goals:**
- No changes to explore session data model, API, or SSE events
- No changes to the tool set (read_file, glob, grep, create_issue, update_issue)
- No changes to frontend UI
- No changes to `runExploreAgent()` function signature or `ExploreAgentContext` interface

## Decisions

### D1: Single flat prompt structure (no section-per-entry-point)

The new `EXPLORE_SYSTEM_PROMPT` will use a flat sectioned structure (Stance → Rhythm → Entry Points → Visualization → Crystallization → Guardrails → Issue Creation) rather than embedding separate sub-prompts for each entry point.

**Rationale:** Token budget (~8000 chars) is tight. Full entry-point examples with ASCII art (like the opencode skill's ~120 lines) would consume too much of the budget. Instead, entry-point guidance will be a concise behavioral rule ("adapt your opening to what the user brings") with brief one-line descriptions of the four patterns.

**Alternatives considered:**
- Full entry-point examples with diagrams (rejected: too long, would exceed token budget)
- External prompt template loaded from file (rejected: adds file I/O complexity for a string constant; the current pattern of inline string works fine)

### D2: Guardrails as positive/negative pairs

The eight guardrails will be written as four concise pairs (Don't/Do) rather than eight separate rules. This is more compact and memorable.

**Rationale:** The opencode skill lists 8 guardrails as 4 "don'ts" + 4 "dos". Pairing them (e.g., "Don't rush → Do let patterns emerge") is more compact in the token budget and creates stronger behavioral contrast.

### D3: `explore.md` gets lighter treatment

The `prompts/explore.md` file serves a different purpose (automated proposal generation in the pipeline). It will be updated to incorporate the same philosophical principles — grounding in codebase, visual thinking for architecture sections, questioning assumptions before proposing — but it will NOT get entry-point differentiation or rhythm control, since it's not a conversational agent.

**Rationale:** `explore.md` is a task instruction ("explore codebase → generate proposal"), not a conversation stance. The sync requirement means sharing the same exploration philosophy, not copying the same structure.

### D4: Preserve mohist-specific crystallization logic

The current prompt's `create_issue` instructions (title/body/labels structure) will be preserved verbatim. Only the *timing* of when to propose crystallization changes (from rigid triggers to natural convergence signals).

**Rationale:** The `create_issue` tool expects a specific issue body format. Changing that would break downstream issue processing. The crystallization timing change is purely behavioral (prompt wording), not structural.

## Risks / Trade-offs

- **[Prompt too long]** → Budget is ~8000 chars. The current prompt is ~1800 chars. The new prompt will be ~3-4x longer. Mitigation: use concise bullet-point style, avoid verbose examples; validate with a character count during implementation.
- **[LLM doesn't follow rhythm instructions]** → Short-turn and don't-rush instructions are soft behavioral guidance; some models may still produce long outputs. Mitigation: the instruction is clear and repeated across sections; if needed, a follow-up change could add a post-processing truncation step, but that's out of scope.
- **[explore.md scope creep]** → Updating explore.md to share philosophical principles could drift into making it a second system prompt. Mitigation: keep changes minimal — add a "How to Explore" section with the six stance principles, leave the rest of the proposal-generation structure intact.

## Migration Plan

1. Rewrite `EXPLORE_SYSTEM_PROMPT` in `explore-agent.ts`
2. Update `explore.md` to incorporate exploration principles
3. Build and verify no TypeScript errors
4. Manual smoke test: start an explore session via the CLI and verify the agent's first response is a short turn (not a wall of text)
5. No database migration, no API version change, no deployment coordination needed — this is a pure prompt swap

## Open Questions

- Should we add a runtime assertion or test that `EXPLORE_SYSTEM_PROMPT.length < 8000`? The spec requires it but it's not clear if it should be a build-time check or just a manual constraint. For now, treating it as a manual constraint during code review.
