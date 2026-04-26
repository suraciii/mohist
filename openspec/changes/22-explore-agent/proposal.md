## Why

The Explore Agent's system prompt produces report-style output that rushes to conclusions instead of acting as a thinking partner. Compared to the opencode explore skill (which has a rich stance model, entry-point differentiation, guardrails, and visual-first habits), the current prompt is too shallow — it lacks rhythm control, assumption questioning, and natural crystallization timing. This makes explore sessions feel like reading a document rather than having a conversation.

## What Changes

- Rewrite `EXPLORE_SYSTEM_PROMPT` in `explore-agent.ts` with a deeper stance model (curious, adaptive, patient, grounded), rhythm control (don't rush, let patterns emerge), and assumption-questioning guidance
- Add entry-point differentiation: adjust opening behavior based on whether the user brings a vague idea, a specific problem, a stuck-in-implementation scenario, or a comparison request
- Add guardrails section (8 rules: don't implement, don't fake understanding, don't rush, don't force structure, don't auto-crystallize, do visualize, do explore codebase, do question assumptions)
- Upgrade visualization guidance from one example to default-first: ASCII diagrams should be the primary tool for clarifying complex relationships, with multiple pattern examples (spectrums, flows, comparisons)
- Replace rigid crystallization trigger with natural timing guidance: propose crystallization when insights organically converge, offer multiple ending modes (flow into proposal, artifact updates, clarity only, continue later)
- Sync `agents/prompts/explore.md` to match the new prompt philosophy

## Capabilities

### New Capabilities

- `explore-agent-prompt` — spec for the Explore Agent system prompt quality: stance model, entry-point handling, rhythm control, guardrails, visualization defaults, and crystallization timing

### Modified Capabilities

_(none — this is a prompt-only change, no API/data model/behavior contracts affected)_

## Impact

- `packages/cli/src/agents/explore-agent.ts` — `EXPLORE_SYSTEM_PROMPT` constant rewritten
- `packages/cli/src/agents/prompts/explore.md` — synced to match new prompt philosophy
- Token cost: new prompt will be longer; needs to stay under ~2K tokens to keep per-turn cost reasonable
- No API, data model, tool set, or frontend changes
