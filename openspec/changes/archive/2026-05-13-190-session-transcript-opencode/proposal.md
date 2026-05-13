## Why

Session transcript is the user's primary window into what the agent is doing, but the current page still reads like a partially normalized event log: reasoning is detached from the surrounding response, many tools appear as `unknown`, and raw JSON forces users to decode tool intent by hand. This change is needed now because repeated UI-only fixes have not closed the gap with opencode-quality transcript readability, leaving debugging, trust, and execution visibility weaker than they should be in Mohist's core workflow surface.

## What Changes

- Upgrade the session transcript page from raw-event presentation to an ordered conversation transcript that preserves the user's mental model of prompt -> thinking -> action -> result.
- Replace whitelist-style tool display with a registry/fallback model so every tool has a readable visible identity, including tools that were previously rendered as `unknown`.
- Render tool inputs and outputs according to tool semantics, so file reads, searches, shell commands, web fetches, and file edits surface the important content without exposing raw JSON as the primary view.
- Improve transcript scanning by grouping adjacent context-gathering tools, collapsing reasoning by default, and making running tool activity visibly distinct from completed activity.
- Add richer transcript affordances expected from a production conversation surface, including readable diff inspection, copyable assistant output, visible model/duration metadata, and live-follow behavior that does not interrupt manual reading.
- Tighten the contract between live session events and replayed transcript data so the page can converge on the same visible structure during streaming and after refresh.

## Capabilities

### New Capabilities

<!-- Leave empty if none. -->

### Modified Capabilities

- `agent-session-ui`
- `pipeline-session-events`

## Impact

- Affects Web UI transcript rendering in `packages/cli/web/src/components/session-transcript/*`, `SessionPage.tsx`, `SessionTranscriptView.tsx`, and `ToolCallCard.tsx`.
- Affects frontend transcript projection and live merge logic in `packages/cli/web/src/lib/session-transcript-display.ts`, `packages/cli/web/src/hooks/useSessionTranscript.ts`, and related shared transcript types.
- May require transcript metadata and ordering improvements in the session-detail API and supporting backend transcript assembly, especially where live SSE updates must match persisted replay behavior.
- May require updates to session event capture and ordering fidelity for reasoning/text/tool interleaving, including current same-second timestamp limitations in session stream persistence.
- Requires updated transcript fixtures and tests for readable tool fallback, grouped context tools, reasoning placement, diff visibility, running-state rendering, and smart auto-scroll behavior.
