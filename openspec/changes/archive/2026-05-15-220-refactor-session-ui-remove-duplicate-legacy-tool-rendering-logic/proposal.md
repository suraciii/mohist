## Why

Session transcript tool rendering still has two sources of truth: the legacy `ToolCallCard` helpers and the newer registry/shared utility path. This makes future tool display fixes risky because labels, arguments, display types, and patch parsing can drift between transcript entry points.

## What Changes

- Remove duplicate tool parsing logic from `ToolCallCard` and route legacy tool card rendering through shared transcript tool utilities and registry rules.
- Keep tool label, argument badge, display type, and patch/file-change parsing behavior centralized in `transcript-tool-utils` and `session-transcript/tool-registry`.
- Preserve the current session transcript reading experience while making legacy session views and registry-based views render the same tool semantics.
- Ensure new tool display rules only need to be added to the shared utility or registry layer.

## Capabilities

### New Capabilities


### Modified Capabilities

- agent-session-ui

## Impact

- Affects frontend session transcript rendering in `packages/cli/web/src/components/ToolCallCard.tsx` and `packages/cli/web/src/components/SessionTranscriptView.tsx`.
- Reuses existing shared tool parsing in `packages/cli/web/src/lib/transcript-tool-utils.ts` and registry behavior in `packages/cli/web/src/components/session-transcript/tool-registry.tsx`.
- No backend data model, API contract, or dependency changes are expected.
- Regression risk is limited to transcript tool presentation, especially known tools such as `bash`, `read`, `grep`, `glob`, `webfetch`, `task`, `skill`, `apply_patch`, `edit`, and `write`.
