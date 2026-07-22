## Why

Session pages currently collapse distinct content-visibility failures into generic empty states, leaving users unable to tell whether a runner never uploaded content or the selected runtime has filtered existing history. Strict realtime matching also drops all live events while key session metadata is temporarily unavailable, making an active session appear empty when useful identifying context still exists.

## What Changes

- Distinguish a session that has started but has never received content from a runtime-specific view whose content is empty because recorded activity belongs to another runtime.
- Show an actionable empty-state hint for a runtime-specific mismatch, directing users to the available historical runtime view instead of implying that the logical session has no activity.
- Keep displaying eligible realtime transcript events when physical runtime-binding metadata is temporarily missing by falling back to the canonical logical AgentSession identity, while continuing to reject events attributable to another session or runtime.
- Preserve the existing server-side transcript filtering and runtime-binding validation semantics; this change does not add a full empty-session diagnostics panel.

## Capabilities

- `agent-session-empty-state-diagnostics`: Session transcript empty states identify whether no content has ever been received or the current runtime view excludes content recorded under another runtime, and provide the corresponding user action.
- `agent-session-live-content-visibility`: Realtime transcript events remain associated with the intended visible session when physical runtime metadata is temporarily incomplete, using available logical session context without admitting events known to belong elsewhere.

## Impact

- **Web session data and view state**: issue-bound and generic session data sources must expose enough transcript and runtime-lineage context for the shared session detail UI to derive the correct empty-state cause.
- **Web realtime transcript handling**: live event identity types and session-event matching in the shared transcript hook must support the constrained logical-session fallback while retaining stale-runtime isolation.
- **Web UI and tests**: shared session transcript empty states and focused session-page/hook coverage will change for never-uploaded, historical-runtime, temporarily incomplete metadata, and definitely mismatched event scenarios.
- **Server, APIs, and dependencies**: no server filtering change, API endpoint change, persistence/schema migration, or new dependency is expected.
