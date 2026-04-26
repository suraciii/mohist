## Why

The plan stage's `selfReviewNotes` and review stage's `reviewReport` store raw LLM thinking/debugging output instead of clean final reports. The review prompt already instructs the agent to write `review.md` to disk, but the workflow controller ignores that file and stores the entire ACP session text accumulation instead.

## What Changes

- After review/plan stages complete, read `{changeDir}/review.md` (or equivalent output file) from disk as the report, falling back to `result.text` only if the file doesn't exist
- Filter `agent_thought_chunk` events in `acp-session.ts` so they don't pollute `agentText`
- Ensure `agentText` only contains final assistant message content, not interleaved reasoning

## Capabilities

### New Capabilities

- `agent-report-extraction`: Extract clean final reports from agent output files on disk instead of raw session text

### Modified Capabilities

- `agent-runtime`: `agentText` accumulation must exclude thought/reasoning chunks from `agent_thought_chunk` events

## Impact

- `packages/cli/src/agent-runtime/acp-session.ts` — filter thought chunks, fix agentText accumulation
- `packages/cli/src/workflow/workflow-controller.ts` — read report from disk file after stage completion
- `packages/cli/src/agents/prompts/review.md` — no changes needed, already instructs file output
- `openspec/specs/agent-runtime/spec.md` — delta for thought chunk filtering requirement
