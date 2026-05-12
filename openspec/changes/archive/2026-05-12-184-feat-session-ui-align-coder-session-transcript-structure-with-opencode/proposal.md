## Why

Mohist's coder session page is still understandable as an internal event log, but it does not yet read like the polished opencode session transcript users expect: prompts, assistant output, tool use, reasoning, and diffs are still fragmented into dashboard-like cards and noisy lifecycle rows. This change is needed now because the dedicated session page already has transcript foundations in place, and users now need the final structural alignment that makes session replay feel like a trustworthy Mohist-to-Coder conversation instead of a workflow debugger.

## What Changes

- Rework the dedicated session page around an opencode-like turn transcript, where each Mohist prompt is the visible turn boundary and the Coder response is rendered as ordered assistant parts beneath it.
- Add a display-oriented transcript adapter so existing session detail data can be projected into prompt, text, reasoning, tool, error, context-group, and diff/file-change parts without requiring a full database redesign.
- Replace heavy event-log-style tool cards with compact tool rows and grouped context gathering, merging started/completed lifecycle updates into one logical tool part and suppressing stale `unknown` placeholders.
- Hide internal transcript noise by default, including `todowrite`, duplicate lifecycle fragments, raw JSON-first payloads, and other debugging-oriented artifacts that are not part of the primary reading flow.
- Make patch, edit, write, and changed-file output belong to the assistant turn itself, with compact summaries by default and expandable per-file diff details when users want audit depth.
- Align live reading behavior with opencode-style transcript affordances, including sticky in-timeline session header, subtle running progress, restrained thinking/loading feedback, bottom-lock auto-follow, and a low-noise jump-to-bottom control.
- Tighten session transcript normalization and API metadata so live streaming and historical replay produce the same visible turn order, tool grouping, statuses, and changed-file sections after refresh.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `agent-session-ui`
- `session-timeline-ui`
- `http-api`
- `coder-session-tracking`
- `pipeline-session-events`

## Impact

- **Frontend session experience**: `packages/cli/web/src/components/SessionPage.tsx`, `SessionTranscriptView.tsx`, `ToolCallCard.tsx`, related hooks such as `useSessionTranscript.ts`, and shared transcript types will shift from readable-but-card-heavy transcript rendering to an opencode-like centered timeline with turn-native parts and low-noise live behavior.
- **Transcript projection**: the frontend will likely introduce or refactor a display adapter layer around `CoderSessionDetail.turns` so prompt blocks, assistant parts, grouped context tools, muted reasoning, tool summaries, and turn-ending diffs can be rendered from one stable display contract.
- **Backend transcript assembly**: `packages/cli/src/services/session-transcript-service.ts` and related session persistence/observer paths must provide stronger tool lifecycle merging, stable statuses, better unknown-tool normalization, hidden internal-tool defaults, and richer file-change metadata for patch/edit/write events.
- **API contract**: `GET /api/issues/:number/coder-sessions/:sessionId` must remain the canonical transcript source while exposing enough normalized metadata for the frontend to render opencode-like turns without re-inferencing core lifecycle state from raw logs.
- **Live convergence**: SSE-driven session updates must continue to append into the same normalized transcript model used by replay so running sessions, refreshes, and completed sessions stay visually consistent.
- **No workflow-model expansion**: this change does not add a composer, workflow dashboard, artifact-first summary surface, or a full opencode pagination/cache system; it narrows the session page to a better read-only transcript experience.
