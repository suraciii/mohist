## Why

Issue #190 improved the session transcript surface, but real session data and follow-up review still show that key transcript behaviors are wrong: thinking is reordered into a block at the top, live sessions lose thinking entirely, tool lifecycle events can split into orphan `unknown` rows, and file-changing tools still fall back to raw JSON. This change is needed now because the dedicated session page is supposed to be a trustworthy Mohist-to-Coder replay, and these gaps make both live debugging and historical audit misleading.

## What Changes

- Preserve transcript ordering at the data-assembly layer so reasoning and assistant text remain interleaved in the order they were emitted instead of being collapsed into giant same-type parts.
- Extend live session SSE coverage to stream coder thinking updates, so running sessions show the same thought flow that historical replay is expected to preserve.
- Fix tool lifecycle correlation so tool start and update events resolve to one logical tool call even when the current stream uses different temporary and provider ids.
- Render file-changing tools such as `edit`, `write`, and `apply_patch` as diff-first transcript content instead of raw JSON payloads, including readable changed-file summaries and expandable diff details.
- Correct known transcript display regressions from the #190 review, including the search-results ellipsis condition, changed-file collection from grouped context tools, duplicate fallback subtitle logic, and unnecessary single-tool context grouping.
- Keep transcript replay and live updates convergent so refreshing after a live run does not materially change tool identity, ordering, or changed-file summaries.

## Capabilities

### New Capabilities

<!-- Leave empty if none. -->

### Modified Capabilities

- `agent-session-ui`
- `pipeline-session-events`
- `coder-session-tracking`

## Impact

- **Backend transcript assembly**: `packages/cli/src/services/session-transcript-service.ts` must close active reasoning/text parts when the opposite stream arrives, preserve interleaving, improve tool-call id reconciliation, and attach diff-oriented metadata for file-changing tools.
- **Runtime/session event emission**: `packages/cli/src/services/session-observers.ts`, `packages/cli/src/services/event-bus.ts`, `packages/cli/src/api/events.ts`, and related runtime wiring must add a live `coder_thought_chunk` event and keep backend/frontend event registries aligned.
- **Frontend live transcript state**: `packages/cli/web/src/hooks/useSessionTranscript.ts`, `packages/cli/web/src/lib/types.ts`, and `packages/cli/web/src/lib/agent-events.ts` must consume live thinking chunks and merge them into the same transcript shape used by persisted replay.
- **Frontend transcript rendering**: `packages/cli/web/src/components/session-transcript/AssistantParts.tsx` and `packages/cli/web/src/lib/session-transcript-display.ts` must render diff-first file changes, stop showing raw JSON as the primary edit/write/apply_patch output, and fix grouped-tool changed-file and search-result display bugs.
- **Dependencies and reuse**: the change can reuse existing diff parsing/rendering utilities already present in `packages/cli/web/src/lib/diffModel.ts` and issue diff components rather than introducing a separate transcript-only diff model.
- **Validation surface**: session transcript tests, live SSE transcript tests, and existing `packages/cli` build/test workflows need coverage for interleaved reasoning/text ordering, live thinking visibility, stable tool merging, diff rendering, and grouped changed-file summaries.
