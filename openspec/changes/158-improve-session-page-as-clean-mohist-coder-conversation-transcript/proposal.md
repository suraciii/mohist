## Why

The dedicated session page still reads like a formatted event log instead of a trustworthy Mohist-to-coder conversation transcript: internal stream fragments can surface as misleading entries such as `unknown running...`, and users cannot quickly answer what Mohist asked, what the coder did, and what changed. This needs to be fixed now because the session page is the audit record for active and historical coder work, and visible projection bugs make users doubt whether the agent is actually working or whether the transcript is broken.

## What Changes

- Normalize raw session events into stable transcript parts before rendering, with `tool_call` and `tool_call_update` merged by provider call id or correlation id.
- Hide step/lifecycle/internal stream events from the visible transcript, keeping them available only as metadata or raw debugging details.
- Prevent orphan pending/update fragments from rendering as separate `unknown running...` cards; running state must only appear for real active tools.
- Preserve a clear conversation turn model where Mohist prompts open turns and coder text, reasoning, semantic tool parts, errors, and output summaries attach to the corresponding assistant response.
- Render reasoning as collapsed or summarized assistant context by default, not as primary transcript prose.
- Aggregate context-gathering tools such as read, grep, glob, search, list, and memory reads into compact summaries with expandable details.
- Present bash tools with a useful title, command, status, duration, concise output preview, and expandable full output.
- Present edit, write, and apply_patch tools with changed-file summaries and expandable raw diff or patch details.
- Show resulting file changes as compact turn/session output, including touched paths and additions/deletions when available, without turning the page into a workflow dashboard.
- Ensure historical replay and live streaming use the same normalized transcript shape so refreshes, live updates, and completed sessions do not create duplicate or orphan tool cards.

## Capabilities

### New Capabilities



### Modified Capabilities

- `agent-session-ui` - the dedicated session page must prioritize an opencode-style read-only conversation transcript with semantic assistant parts, grouped context gathering, useful tool summaries, collapsed raw details, and compact changed-file output.
- `session-timeline-ui` - session rendering must stop exposing raw event-log fragments and must preserve readable Mohist/coder turns during both historical replay and live streaming.
- `coder-session-tracking` - coder session events must provide stable tool identities, statuses, titles, inputs, outputs, and file-change metadata needed to merge tool lifecycle events accurately.
- `pipeline-session-events` - streamed ACP/session events must remain compatible with the normalized transcript projection and avoid emitting or replaying lifecycle fragments as visible tools.
- `http-api` - session detail responses must expose a normalized transcript shape that is sufficient for consistent historical and live rendering, including transcript warnings or raw-debug access when normalization is incomplete.

## Impact

- **Backend transcript projection**: `packages/cli/src/services/session-transcript-service.ts` needs stricter event projection, tool lifecycle merging, status normalization, internal-event filtering, context grouping inputs, and changed-file extraction for edit/write/patch tools.
- **Session persistence**: `packages/cli/src/db/session-stream-log-repo.ts`, session stream writes, and fallback workflow log usage must preserve enough correlation data for refresh-safe tool merging across historical sessions.
- **Session observers and events**: `packages/cli/src/services/session-observers.ts` and agent runtime observer paths must continue writing Mohist prompts and assistant/tool events while avoiding visible lifecycle leakage.
- **API**: `GET /api/issues/:number/coder-sessions/:sessionId` in `packages/cli/src/api/issues.ts` must return the normalized transcript, metadata, warnings, and file-change summaries used by the session page.
- **Frontend live transcript**: `packages/cli/web/src/hooks/useSessionTranscript.ts` must apply the same merge rules as historical replay for SSE updates so live tools update in place rather than duplicating.
- **Frontend rendering**: `packages/cli/web/src/components/SessionPage.tsx`, `SessionTranscriptView.tsx`, `ToolCallCard.tsx`, and shared types in `packages/cli/web/src/lib/types.ts` must render semantic transcript parts, grouped context tools, restrained running states, expandable raw details, and compact file changes.
- **Tests**: Existing transcript service and session page tests under `packages/cli/tests/` and `packages/cli/web/tests/` need coverage for pending/update tool merges, unknown-tool fallbacks, context aggregation, live refresh behavior, and changed-file summaries.
- **No breaking user workflow**: routes, workflow stages, and agent control behavior stay unchanged; this proposal changes transcript projection and display quality, not pipeline control or conversation continuation.
