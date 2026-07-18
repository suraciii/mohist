## Why

The Session page repeats identity, status, and navigation across several persistent rows, leaving too little of the first viewport for the transcript users came to read. The page frame needs a clearer hierarchy now so users can understand the session at a glance and begin reading without first sorting through duplicated information.

## What Changes

- Consolidate the session name, status, stage, model, turn count, timestamps, duration, and other available summary metadata into a compact single-line header, with the session ID available as a one-click copy target for its complete value.
- Remove the always-visible duplicate title strip; show a compact sticky session name and status only after the primary header has scrolled out of view.
- Reduce the visual priority of dangerous or infrequent actions such as Cancel session, and explain why unavailable Compact, Reset, and similar actions cannot currently be used.
- Present sibling-session navigation only once per viewport: use the status-rich sibling sidebar where it is available, with compact previous/next navigation as the narrow-screen fallback.
- Show an absolute timestamp for sessions that ended long enough ago that a relative value would require date calculation, while retaining the relative time as supplementary hover information.
- Make the followup composer communicate the session's actual interaction state: ready for input, queued/sending, or ended and unable to accept another message.
- Preserve transcript rendering, sibling-sidebar content, session action semantics, and all existing server APIs.

## Capabilities

- `coder-session-evidence-view`: Refines the Session page's information frame so identity and status are compact and non-duplicative, transcript content begins within the first viewport, navigation and actions have clear priority, time and session identity are directly usable, and followup availability is represented consistently with session state across workflow and generic session detail pages.

## Impact

- **Web Session page:** the shared session detail shell and header/sticky composition in `packages/web/src/pages/session/ui/`, affecting both issue/workflow and generic session detail routes.
- **Web session controls:** followup and recovery action presentation in `packages/web/src/widgets/coder-session/ui/`, plus issue-session sibling navigation composition in `packages/web/src/pages/session/data/`.
- **Tests:** Session page header, sticky behavior, responsive sibling navigation, action-state explanations, timestamp/copy behavior, and followup composer state coverage in `packages/web/src/pages/session/` and `packages/web/tests/`.
- **Unaffected systems:** transcript content rendering owned by issue #427, sibling-sidebar entry content, server/runner/CLI behavior, API and DTO contracts, persistence, and external dependencies.
